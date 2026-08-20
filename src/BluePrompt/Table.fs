/// テーブルをGFMのパイプテーブルで表現できて、データとして読める形へ変形するDOM操作。
/// GFMのパイプテーブルは結合セルもセル内改行も表現できず、
/// pandocが変換を諦めてテーブル全体を[TABLE]という文字列へ潰してしまうため、
/// 変換前にDOMの段階で表を平坦化する。
module BluePrompt.Table

open System
open System.Collections.Generic
open AngleSharp.Dom
open AngleSharp.Html.Dom

/// 見出し要素のセレクタ。
/// 見出しを含む表はデータの表ではなくレイアウトなので変形の対象から外す判定に使う。
let private headingSelector = "h1, h2, h3, h4, h5, h6"

/// タグを外してはいけないブロックが含む構造要素のセレクタ。
/// ブロックごとのループの中で文字列を組み立て直さないように定数として持つ。
let private structureSelector = $"%s{headingSelector}, table, ul, ol"

/// 表全体を横断する1セルだけの行(小見出しや自由記述)を段落として表の外へ出し、
/// 表をその前後で分割する。
/// 列を持つ行と同じ表に混ぜると、長い記述に合わせた列幅の整形で他の行が際限なく伸びるため。
/// 全行が1セルの表は1列の表として意味を持つのでそのまま残す。
/// 見出しを含む表はデータの表ではなくレイアウトなので触らない。
let private splitDividerRows (document: IDocument) : unit =
    for table in Seq.toArray (document.QuerySelectorAll "table") do
        if isNull (table.QuerySelector headingSelector) then
            let rows = Seq.toArray (table :?> IHtmlTableElement).Rows

            // 1セルだけに見える行には上からのrowspanで埋まる継続行もあるため、
            // colspanで横に広がっている行だけを区切り行とみなす。
            let isDivider (row: IHtmlTableRowElement) =
                row.Cells.Length = 1 && 1 < row.Cells[0].ColumnSpan

            if Array.exists isDivider rows && not (Array.forall isDivider rows) then
                let fragments = ResizeArray<INode>()
                let pendingRows = ResizeArray<INode>()

                let flushRows () =
                    if 0 < pendingRows.Count then
                        let segment = document.CreateElement "table"
                        segment.Append(pendingRows.ToArray())
                        fragments.Add segment
                        pendingRows.Clear()

                for row in rows do
                    if isDivider row then
                        flushRows ()
                        let cell = row.Cells[0]
                        let paragraph = document.CreateElement "p"

                        if cell.TagName = "TH" then
                            // 見出しセルだった行は小見出しなので、強調で見出しらしさを残す。
                            let emphasis = document.CreateElement "strong"
                            emphasis.Append(Seq.toArray cell.ChildNodes)
                            paragraph.Append [| emphasis :> INode |]
                        else
                            paragraph.Append(Seq.toArray cell.ChildNodes)

                        fragments.Add paragraph
                    else
                        pendingRows.Add row

                flushRows ()
                table.Replace(fragments.ToArray())

/// 改行の境界をどう繋ぐか決める。
/// 前が句読点や開き括弧などで終わるか、後が区切り記号や閉じ括弧などで始まるなら、
/// そのまま詰めても読めるのでNoneを返す。どちらでもなければ挟む読点を返す。
/// 対象が日本語のwikiであることを前提にした結合規則。
let private joinSeparator (previousText: string) (nextText: string) : string option =
    let flushBefore = "。．！？!?…、,/／・:：(（「『"
    let flushAfter = "/／・、。,)）」』(（"

    let flushable =
        (previousText <> "" && flushBefore.Contains previousText[previousText.Length - 1])
        || (nextText <> "" && flushAfter.Contains nextText[0])

    if flushable then None else Some "、"

/// 兄弟をnextの方向へ辿って最初に中身のあるノードを、トリム済みテキストと組で返す。
/// TextContentはアクセスごとに部分木を走査して文字列を生成するため、
/// 見つけたノードのテキストを呼び出し側が取り直さずに済むように一緒に返す。
/// 走査中の空判定はトリム文字列を確保しないIsNullOrWhiteSpaceで行う。
let private siblingWithContent (next: INode -> INode) (node: INode) : (INode * string) option =
    let rec go (sibling: INode) =
        if isNull sibling then
            None
        else
            let text = sibling.TextContent

            if String.IsNullOrWhiteSpace text then
                go (next sibling)
            else
                Some(sibling, text.Trim())

    go (next node)

/// 兄弟を遡って最初に中身のあるノードをテキストと組で返す。
let private precedingNode (node: INode) : (INode * string) option =
    siblingWithContent (fun sibling -> sibling.PreviousSibling) node

/// 兄弟を下って最初に中身のあるノードをテキストと組で返す。
let private followingNode (node: INode) : (INode * string) option =
    siblingWithContent (fun sibling -> sibling.NextSibling) node

/// 長いリンクラベルを同じリンク先の複数のaへ分けて折り返す書き方があり、
/// その境界はひと続きのラベルの途中なので区切りを挟んではいけない。
let private isSameLinkFold (previousNode: INode) (nextNode: INode) : bool =
    match previousNode, nextNode with
    | (:? IElement as previous), (:? IElement as next) ->
        previous.TagName = "A"
        && next.TagName = "A"
        && (match previous.GetAttribute "href" with
            | null -> false
            | href -> href = next.GetAttribute "href")
    | _ -> false

/// セル内のbrを前後の文脈に合わせて詰めるか読点で繋ぐ。
/// GFMのパイプテーブルはセル内の改行を表現できないため。
let private joinCellLineBreaks (document: IDocument) : unit =
    for br in Seq.toArray (document.QuerySelectorAll "th br, td br") do
        match precedingNode br, followingNode br with
        | Some(previousNode, previous), Some(nextNode, next) ->
            if not (isNull (br.Closest "th")) || isSameLinkFold previousNode nextNode then
                // 見出しセルは文ではなくラベルで、改行は表示幅の都合の折り返しでしかない。
                br.Remove()
            else
                match joinSeparator previous next with
                | None -> br.Remove()
                | Some separator -> br.Replace [| document.CreateTextNode separator :> INode |]
        | _ ->
            // セル先頭や末尾のbr(画像除去の跡など)は繋ぐ相手がいないので取り除く。
            br.Remove()

/// セル内のdivやpなどのブロック要素の境界を繋いでタグを外す。
/// ブロック要素が残っていると、
/// pandocがパイプテーブルで表現できずテーブル全体を[TABLE]へ潰してしまうため。
/// QuerySelectorAllは文書順(親が先)なので、逆順に走査して内側から外す。
/// ただしページ全体をテーブルで組むレイアウトでは、本文のあらゆるブロックがセルの子孫になる。
/// 見出しやテーブルなどの構造を含むブロックと、
/// 見出しを含むテーブル(データの表ではなくレイアウト)の配下は外さない。
let private unwrapCellBlocks (document: IDocument) : unit =
    let cellBlocks =
        Seq.toArray (document.QuerySelectorAll "th div, th p, td div, td p")

    // ページ全体をテーブルで組むレイアウトでは同じテーブル配下に大量のブロックが並び、
    // ブロックごとに部分木を引き直すと同じ走査を何百回も繰り返すため、
    // テーブルごとの見出し有無をメモ化する。
    // タグ外しは子ノードをそのまま残すので、見出しの有無は走査中に変わらない。
    let tableHasHeading = Dictionary<IElement, bool>()

    let isLayoutTable (table: IElement) : bool =
        match tableHasHeading.TryGetValue table with
        | true, hasHeading -> hasHeading
        | false, _ ->
            let hasHeading = not (isNull (table.QuerySelector headingSelector))
            tableHasHeading[table] <- hasHeading
            hasHeading

    for block in Array.rev cellBlocks do
        // メモ化済みで安価なレイアウトテーブル判定を先に見て、
        // レイアウト配下のブロックには部分木走査になるQuerySelectorを行わない。
        // レイアウトテーブルのページでは外側のブロックほど部分木がページ全体に近く、
        // 全ブロックで走査すると実質2乗のコストになるため。
        let inLayoutTable =
            match block.Closest "table" with
            | null -> false
            | enclosingTable -> isLayoutTable enclosingTable

        if not inLayoutTable && isNull (block.QuerySelector structureSelector) then
            let text = block.TextContent.Trim()

            if text <> "" && isNull (block.Closest "th") then
                match precedingNode block with
                | None -> ()
                | Some(_, previous) ->
                    match joinSeparator previous text with
                    | None -> ()
                    | Some separator ->
                        block.Before [| document.CreateTextNode separator :> INode |]

            block.Replace(Seq.toArray block.ChildNodes)

/// rowspan/colspan展開の格子の1マス。どのセルがどの位置を占めるかを表す。
type private GridEntry =
    {
        /// この位置を占める元のセル。
        Cell: IHtmlTableCellElement
        /// このマスが元のセル自身の位置(左上)か。元の位置だけがセルをそのまま使う。
        IsOrigin: bool
        /// 縦方向(rowspan由来)の複製だけがテキストを引き継ぐ。
        /// 縦方向の展開で増える位置は行の見出しとして意味があるが、
        /// 横方向の展開で増える位置は同じ行を読めば分かる繰り返しでしかないため。
        KeepsText: bool
    }

/// colspanの切り詰め上限。
/// rowspan/colspanは外部HTML由来の未検証値で、HTML仕様上は65534と1000まで指定できる。
/// そのまま使うと悪意ある値や編集ミスで格子が数千万要素へ膨らむため、
/// rowspanは実際の残り行数で、colspanは現実のwikiテーブルを大きく超えるこの定数で切り詰める。
let private maxColumnSpan = 100

/// 格子の総エントリ数の上限。
/// 1セルあたりの切り詰めだけでは総量が行数×1行のセル数×rowSpan×colSpanで二次的に膨らむため、
/// 見積りがこれを超える表は展開を諦める。現実のwikiテーブルを大きく超える値。
let private maxGridEntries = 1_000_000L

/// セルが格子で占める行方向と列方向の広がり。
/// HTML仕様のrowspan="0"は「セクションの最後まで」を意味しDOMのrowSpanは0を返すため、
/// 仕様に沿って残り行数へ展開する。それ以外のrowspanは残り行数で、colspanは定数で切り詰める。
let private cellSpans (rowCount: int) (rowIndex: int) (cell: IHtmlTableCellElement) : int * int =
    let rowSpan =
        if cell.RowSpan = 0 then
            rowCount - rowIndex
        else
            min cell.RowSpan (rowCount - rowIndex)

    rowSpan, min cell.ColumnSpan maxColumnSpan

/// 子ノードを全て捨てて、渡したノードだけを子にする。
/// InnerHtmlへの空文字列代入は空でもHTMLフラグメントパーサを起動するため、
/// DOM操作だけで取り除く。
let private replaceChildren (parent: IElement) (children: IElement array) : unit =
    while parent.HasChildNodes do
        parent.RemoveChild parent.FirstChild |> ignore

    parent.Append(Array.map (fun child -> child :> INode) children)

/// rowspan/colspanを考慮した格子を組み立て、結合セルを個別セルへ複製展開する。
/// 複製はサブツリーをコピーしない。
/// 深いコピーを繰り返すと入れ子テーブルを含むセルで乗算的に膨らむため。
/// 画像の除去などで全セルが空になった行はノイズでしかないので取り除く。
/// 総エントリ数の見積りが上限を超える表は、
/// メモリ枯渇やハングを防ぐため展開せずそのまま残す。
let private expandMergedCells (document: IDocument) (table: IHtmlTableElement) : bool =
    let rows = Seq.toArray table.Rows

    let estimatedEntries =
        rows
        |> Array.mapi (fun rowIndex row ->
            row.Cells
            |> Seq.sumBy (fun cell ->
                let rowSpan, columnSpan = cellSpans rows.Length rowIndex cell
                int64 rowSpan * int64 columnSpan))
        |> Array.sum

    if maxGridEntries < estimatedEntries then
        false
    else

        // 列インデックスは0から連続する密な整数なので、
        // マスごとに木ノードを確保するSortedDictionaryではなく行ごとの密な配列で格子を持つ。
        // 挿入はO(1)で、走査も挿入順ではなく列順が自然に得られる。
        let grid = Array.init rows.Length (fun _ -> ResizeArray<GridEntry voption>())

        let setEntry (rowEntries: ResizeArray<GridEntry voption>) (index: int) (entry: GridEntry) =
            while rowEntries.Count <= index do
                rowEntries.Add ValueNone

            rowEntries[index] <- ValueSome entry

        rows
        |> Array.iteri (fun rowIndex row ->
            let rowEntries = grid[rowIndex]
            let mutable columnIndex = 0

            for cell in Seq.toArray row.Cells do
                while columnIndex < rowEntries.Count && rowEntries[columnIndex].IsSome do
                    columnIndex <- columnIndex + 1

                let rowSpan, columnSpan = cellSpans rows.Length rowIndex cell

                for i in 0 .. rowSpan - 1 do
                    for j in 0 .. columnSpan - 1 do
                        setEntry
                            (grid[rowIndex + i])
                            (columnIndex + j)
                            { Cell = cell
                              IsOrigin = i = 0 && j = 0
                              KeepsText = j = 0 }

                columnIndex <- columnIndex + columnSpan)

        // 元セルのTextContentはアクセスごとに部分木を走査して文字列を新規生成するため、
        // 元セルごとに一度だけ計算してキャッシュし、rowspan分の複製の生成で使い回す。
        let cellTexts = Dictionary<IHtmlTableCellElement, string>()

        let cellText (cell: IHtmlTableCellElement) : string =
            match cellTexts.TryGetValue cell with
            | true, text -> text
            | false, _ ->
                let text = cell.TextContent
                cellTexts[cell] <- text
                text

        rows
        |> Array.iteri (fun rowIndex row ->
            // セルと空判定の組で持ち回り、
            // 生成した複製のTextContentを空行判定のために走査し直さない。
            let cells =
                grid[rowIndex]
                |> Seq.choose ValueOption.toOption
                |> Seq.map (fun entry ->
                    if entry.IsOrigin then
                        entry.Cell.RemoveAttribute "rowspan" |> ignore
                        entry.Cell.RemoveAttribute "colspan" |> ignore
                        entry.Cell :> IElement, String.IsNullOrWhiteSpace(cellText entry.Cell)
                    else
                        let text = if entry.KeepsText then cellText entry.Cell else ""
                        let copy = document.CreateElement entry.Cell.LocalName
                        copy.TextContent <- text
                        copy, String.IsNullOrWhiteSpace text)
                |> Seq.toArray

            if Array.forall snd cells then
                row.Remove()
            else
                replaceChildren row (Array.map fst cells))

        true

/// 見出しの下が全て空になった列を列ごと取り除く。
/// 画像の除去などで空になった列はノイズでしかないため。
/// ただし2行の表では空セルがデータの欠けを意味しうるので、
/// 見出しに中身のある列は3行以上の表でだけ取り除く。
/// 先頭行を見出し行として扱えるのは全セルがthの場合だけで、
/// データ行から始まる表で先頭行にだけ値がある列を消すと事実データが落ちる。
let private dropEmptyColumns (table: IHtmlTableElement) : unit =
    let survivingRows = Seq.toArray table.Rows

    if 2 <= survivingRows.Length then
        // AngleSharpのIHtmlCollectionはLengthもインデクサも呼ぶたびに子要素を線形に舐め、
        // TextContentもアクセスごとに部分木を走査して文字列を新規生成するため、
        // セルとそのテキストを一度だけ配列へ落として判定に使う。
        // 列単位のセル削除は残った列のセルの位置を変えないので、事前に落とした配列のまま扱える。
        let rowCells = survivingRows |> Array.map (fun row -> Seq.toArray row.Cells)

        let rowTexts =
            rowCells
            |> Array.map (Array.map (fun (cell: IHtmlTableCellElement) -> cell.TextContent.Trim()))

        let hasHeaderRow = rowCells[0] |> Array.forall (fun cell -> cell.TagName = "TH")
        let columnCount = rowCells |> Array.map Array.length |> Array.max

        // 行によってセル数が違うため、短い行では列が無いことを空として扱う。
        let cellTextAt (index: int) (texts: string array) : string =
            if index < texts.Length then texts[index] else ""

        for index in columnCount - 1 .. -1 .. 0 do
            let headerIsEmpty = cellTextAt index rowTexts[0] = ""

            let bodyIsEmpty =
                rowTexts |> Seq.skip 1 |> Seq.forall (fun texts -> cellTextAt index texts = "")

            if bodyIsEmpty && (headerIsEmpty || (hasHeaderRow && 3 <= survivingRows.Length)) then
                for cells in rowCells do
                    if index < cells.Length then
                        cells[index].Remove()

/// キーと値のペアを横に繰り返すレイアウトの行(th,td,th,td,...)を1行1ペアの2列へ正規化する。
/// 列の意味が揃わずデータとして混乱するため。
/// 行末尾の空セルは幅合わせの埋め草なので、ペアの判定から除いて捨てる。
/// 中身が両方空のペアも行にしない。
let private normalizePairsRows (document: IDocument) (table: IHtmlTableElement) : unit =
    for row in Seq.toArray table.Rows do
        let cells = Seq.toArray row.Cells

        // TextContentはアクセスごとに部分木走査と文字列生成を伴うため、
        // 行の先頭でセルごとの空判定を一度だけ配列へ落として使い回す。
        let cellIsBlank =
            cells
            |> Array.map (fun (cell: IHtmlTableCellElement) ->
                String.IsNullOrWhiteSpace cell.TextContent)

        let pairLength =
            match Array.tryFindIndexBack not cellIsBlank with
            | None -> 0
            | Some lastIndex -> lastIndex + 1

        let pairCells = Array.sub cells 0 pairLength

        let isPairsRow =
            4 <= pairCells.Length
            && pairCells.Length % 2 = 0
            && pairCells
               |> Array.indexed
               |> Array.forall (fun (index, cell) ->
                   cell.TagName = (if index % 2 = 0 then "TH" else "TD"))

        if isPairsRow then
            let pairRows =
                pairCells
                |> Array.indexed
                |> Array.chunkBySize 2
                |> Array.choose (fun pair ->
                    let keyIndex, key = pair[0]
                    let _, value = pair[1]

                    if cellIsBlank[keyIndex] && cellIsBlank[keyIndex + 1] then
                        None
                    else
                        let pairRow = document.CreateElement "tr"
                        pairRow.Append [| key :> INode; value |]
                        Some(pairRow :> INode))

            // 末尾のセルは必ず非空なので、最後のペアが常に残りpairRowsが空になることはない。
            row.Replace pairRows

/// 文書中の全テーブルを平坦化する。
/// 区切り行の段落化とセル内改行・ブロック要素の結合を施した後に、
/// 結合セルの複製展開と空行・空列の除去とキー値ペア行の正規化を行う。
/// 列位置はrowspan/colspanを考慮した格子を組み立てて求める。
/// 内側のテーブルを先に処理しないと外側のセル複製で処理前の姿が固定されるため、逆順に走査する。
let flatten (document: IDocument) : unit =
    splitDividerRows document
    joinCellLineBreaks document
    unwrapCellBlocks document

    for table in Array.rev (Seq.toArray (document.QuerySelectorAll "table")) do
        let table = table :?> IHtmlTableElement

        // 展開を諦めた過大な表は結合セルが残ったままで、
        // 後段の列単位の処理も同じ規模で膨らむため丸ごと飛ばす。
        if expandMergedCells document table then
            dropEmptyColumns table
            normalizePairsRows document table
