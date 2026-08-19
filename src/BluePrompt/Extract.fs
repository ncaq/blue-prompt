/// 取得済みHTMLからの本文抽出とDOM変形。
/// ヘッダやサイドバーや広告を除いた本文だけをナレッジ用に抜き出す用途を想定している。
/// ブラウザ上のJavaScriptではなくAngleSharpによるF#実装なので、ブラウザ無しで検証できる。
module BluePrompt.Extract

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open AngleSharp.Dom
open AngleSharp.Html.Dom
open AngleSharp.Html.Parser

/// コンテンツ抽出でどのセレクタも要素に一致しなかった時のURLとセレクタ一覧。
exception ContentNotFound of url: Uri * selectors: string list

/// コンテンツ抽出の設定。
type ContentQuery =
    {
        /// 抽出する要素のCSSセレクタ。
        /// 複数指定した場合はセレクタの列挙順に一致要素のHTMLを連結する。
        ContentSelectors: string list
        /// 抽出前にDOMから取り除く要素のCSSセレクタ。
        RemoveSelectors: string list
        /// aタグを外して中身だけ残すか。
        /// ページ内アンカーや相対リンクはページの外へ持ち出すと辿れず、ノイズにしかならない。
        UnwrapLinks: bool
        /// imgをalt属性のテキストへ置き換えるか。
        /// 素材や品物を画像で表現しているページでは、altに入っている名前が唯一の事実になる。
        /// altが空の画像と、altがただのファイル名でしかない画像は取り除く。
        /// falseの場合は何もしないので、画像を消したい時はRemoveSelectorsに"img"を入れる。
        ReplaceImagesWithAlt: bool
        /// テーブルのrowspan/colspanを個別セルへ展開し、セル内の改行やブロック要素を1行へ繋ぐか。
        /// GFMのパイプテーブルは結合セルもセル内改行も表現できず、
        /// pandocが変換を諦めてテーブル全体を[TABLE]という文字列へ潰してしまうため。
        FlattenTables: bool
    }

/// 一致した要素をDOMから取り除く。
let private removeElements (document: IDocument) (selector: string) : unit =
    for element in Seq.toArray (document.QuerySelectorAll selector) do
        element.Remove()

/// 一致した要素のタグを外して子ノードだけ残す。
let private unwrapElements (document: IDocument) (selector: string) : unit =
    for element in Seq.toArray (document.QuerySelectorAll selector) do
        element.Replace(Seq.toArray element.ChildNodes)

/// altがただのファイル名(画像拡張子で終わる)かどうかの判定。
let private fileNameAltPattern =
    Regex(@"\.(png|jpe?g|gif|webp|svg|avif)$", RegexOptions.IgnoreCase)

/// imgをalt属性のテキストへ置き換える。
/// altが空の画像と、altがただのファイル名の画像は取り除く。
let private replaceImagesWithAlt (document: IDocument) : unit =
    for image in Seq.toArray (document.QuerySelectorAll "img") do
        let alt =
            match image.GetAttribute "alt" with
            | null -> ""
            | alt -> alt.Trim()

        if alt = "" || fileNameAltPattern.IsMatch alt then
            image.Remove()
        else
            image.Replace [| document.CreateTextNode alt :> INode |]

/// 見出し要素のセレクタ。
/// 見出しを含む表はデータの表ではなくレイアウトなので変形の対象から外す判定に使う。
let private headingSelector = "h1, h2, h3, h4, h5, h6"

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

/// 兄弟をnextの方向へ辿って最初に中身のあるノードを返す。
let private siblingWithContent (next: INode -> INode) (node: INode) : INode option =
    let rec go (sibling: INode) =
        if isNull sibling then None
        elif sibling.TextContent.Trim() <> "" then Some sibling
        else go (next sibling)

    go (next node)

/// 兄弟を遡って最初に中身のあるノードを返す。
let private precedingNode (node: INode) : INode option =
    siblingWithContent (fun sibling -> sibling.PreviousSibling) node

/// 兄弟を下って最初に中身のあるノードを返す。
let private followingNode (node: INode) : INode option =
    siblingWithContent (fun sibling -> sibling.NextSibling) node

/// ノードの本文テキスト。ノードが無ければ空文字列。
let private nodeText (node: INode option) : string =
    match node with
    | None -> ""
    | Some node -> node.TextContent.Trim()

/// 長いリンクラベルを同じリンク先の複数のaへ分けて折り返す書き方があり、
/// その境界はひと続きのラベルの途中なので区切りを挟んではいけない。
let private isSameLinkFold (previousNode: INode option) (nextNode: INode option) : bool =
    match previousNode, nextNode with
    | Some(:? IElement as previous), Some(:? IElement as next) ->
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
        let previousNode = precedingNode br
        let nextNode = followingNode br
        let previous = nodeText previousNode
        let next = nodeText nextNode

        if previous = "" || next = "" then
            // セル先頭や末尾のbr(画像除去の跡など)は繋ぐ相手がいないので取り除く。
            br.Remove()
        elif not (isNull (br.Closest "th")) || isSameLinkFold previousNode nextNode then
            // 見出しセルは文ではなくラベルで、改行は表示幅の都合の折り返しでしかない。
            br.Remove()
        else
            match joinSeparator previous next with
            | None -> br.Remove()
            | Some separator -> br.Replace [| document.CreateTextNode separator :> INode |]

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
        let containsStructure =
            not (isNull (block.QuerySelector $"%s{headingSelector}, table, ul, ol"))

        let inLayoutTable =
            match block.Closest "table" with
            | null -> false
            | enclosingTable -> isLayoutTable enclosingTable

        if not containsStructure && not inLayoutTable then
            let text = block.TextContent.Trim()
            let previous = nodeText (precedingNode block)

            if previous <> "" && text <> "" && isNull (block.Closest "th") then
                match joinSeparator previous text with
                | None -> ()
                | Some separator -> block.Before [| document.CreateTextNode separator :> INode |]

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

    let grid = Array.init rows.Length (fun _ -> SortedDictionary<int, GridEntry>())

    rows
    |> Array.iteri (fun rowIndex row ->
        let mutable columnIndex = 0

        for cell in Seq.toArray row.Cells do
            while grid[rowIndex].ContainsKey columnIndex do
                columnIndex <- columnIndex + 1

            let rowSpan, columnSpan = cellSpans rows.Length rowIndex cell

            for i in 0 .. rowSpan - 1 do
                for j in 0 .. columnSpan - 1 do
                    grid[rowIndex + i][columnIndex + j] <-
                        { Cell = cell
                          IsOrigin = i = 0 && j = 0
                          KeepsText = j = 0 }

            columnIndex <- columnIndex + columnSpan)

    rows
    |> Array.iteri (fun rowIndex row ->
        let cells =
            grid[rowIndex].Values
            |> Seq.map (fun entry ->
                if entry.IsOrigin then
                    entry.Cell.RemoveAttribute "rowspan" |> ignore
                    entry.Cell.RemoveAttribute "colspan" |> ignore
                    entry.Cell :> IElement
                else
                    let copy = document.CreateElement entry.Cell.LocalName

                    copy.TextContent <- (if entry.KeepsText then entry.Cell.TextContent else "")

                    copy)
            |> Seq.toArray

        if Array.forall (fun (cell: IElement) -> cell.TextContent.Trim() = "") cells then
            row.Remove()
        else
            replaceChildren row cells)

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
        let hasHeaderRow =
            survivingRows[0].Cells |> Seq.forall (fun cell -> cell.TagName = "TH")

        let columnCount =
            survivingRows |> Array.map (fun row -> row.Cells.Length) |> Array.max

        for index in columnCount - 1 .. -1 .. 0 do
            let headerIsEmpty =
                survivingRows[0].Cells.Length <= index
                || survivingRows[0].Cells[index].TextContent.Trim() = ""

            let bodyIsEmpty =
                survivingRows
                |> Array.indexed
                |> Array.forall (fun (rowIndex, row) ->
                    rowIndex = 0
                    || row.Cells.Length <= index
                    || row.Cells[index].TextContent.Trim() = "")

            if bodyIsEmpty && (headerIsEmpty || (hasHeaderRow && 3 <= survivingRows.Length)) then
                for row in survivingRows do
                    if index < row.Cells.Length then
                        row.Cells[index].Remove()

/// キーと値のペアを横に繰り返すレイアウトの行(th,td,th,td,...)を1行1ペアの2列へ正規化する。
/// 列の意味が揃わずデータとして混乱するため。
/// 行末尾の空セルは幅合わせの埋め草なので、ペアの判定から除いて捨てる。
/// 中身が両方空のペアも行にしない。
let private normalizePairsRows (document: IDocument) (table: IHtmlTableElement) : unit =
    for row in Seq.toArray table.Rows do
        let cells = Seq.toArray row.Cells

        let pairCells =
            match
                Array.tryFindIndexBack
                    (fun (cell: IHtmlTableCellElement) -> cell.TextContent.Trim() <> "")
                    cells
            with
            | None -> [||]
            | Some lastIndex -> Array.sub cells 0 (lastIndex + 1)

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
                |> Array.chunkBySize 2
                |> Array.choose (fun pair ->
                    let key = pair[0]
                    let value = pair[1]

                    if key.TextContent.Trim() = "" && value.TextContent.Trim() = "" then
                        None
                    else
                        let pairRow = document.CreateElement "tr"
                        pairRow.Append [| key :> INode; value |]
                        Some(pairRow :> INode))

            if Array.isEmpty pairRows then
                row.Remove()
            else
                row.Replace pairRows

/// テーブルをGFMのパイプテーブルで表現できて、データとして読める形へ変形する。
/// 区切り行の段落化とセル内改行・ブロック要素の結合を施した後に、
/// 結合セルの複製展開と空行・空列の除去とキー値ペア行の正規化を行う。
/// 列位置はrowspan/colspanを考慮した格子を組み立てて求める。
/// 内側のテーブルを先に処理しないと外側のセル複製で処理前の姿が固定されるため、逆順に走査する。
let private flattenTables (document: IDocument) : unit =
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

/// HTML文字列からqueryに従って本文だけをHTML文字列として抜き出す。
/// RemoveSelectorsの除去とUnwrapLinks・FlattenTablesの変形を施した後に、
/// ContentSelectorsへ一致した要素のouterHTMLを連結して返す。
/// 一致する要素が無いセレクタは読み飛ばす。
/// wikiruの脚注のように存在しないことが正常な要素があるためで、
/// RemoveSelectorsの0件一致も同様に正常として扱う。
/// ただし全ContentSelectorsが1件も一致しなかった場合は、
/// サイト側の構造変更で抽出が全滅した可能性が高く、
/// 空のナレッジによる上書きを防ぐためContentNotFoundを送出する。
/// urlは例外へ抽出元を載せるための情報で、取得そのものはこの関数の外で済ませておく。
let contentHtml (url: Uri) (query: ContentQuery) (html: string) : string =
    use document = HtmlParser().ParseDocument html

    for selector in query.RemoveSelectors do
        removeElements document selector

    if query.ReplaceImagesWithAlt then
        replaceImagesWithAlt document

    // 平坦化はセル内改行の結合で同じリンク先の折り返しを見分けるため、
    // リンクを外す前に行う。
    if query.FlattenTables then
        flattenTables document

    if query.UnwrapLinks then
        unwrapElements document "a"

    let contents =
        query.ContentSelectors
        |> List.collect (fun selector ->
            document.QuerySelectorAll selector
            |> Seq.map (fun element -> element.OuterHtml)
            |> Seq.toList)

    if List.isEmpty contents then
        raise (ContentNotFound(url = url, selectors = query.ContentSelectors))

    String.concat "\n" contents
