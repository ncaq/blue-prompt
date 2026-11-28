/// 表を、列の位置が揃った格子へ組み直す。
/// 結合セルもセル内改行もそのままでは1行1項目の箇条書きにできないため、
/// 表を読み解く前に、行ごとのセルの並びが列と対応する形へ平らにする。
module BluePrompt.Grid

open System
open BluePrompt.Article

/// 格子の1マス。
type GridCell =
    {
        /// 見出しセル(th)か。
        IsHeader: bool
        Content: CellContent
        /// トリム済みの文字。判定のたびに並びを辿り直さないように持つ。
        Text: string
        /// 画像を含むか。文字を持たず画像だけのセルも値として意味を持つ。
        HasImage: bool
        /// 上の行から縦の結合で伸びてきたセルの複製か。
        /// 中身がたまたま上と同じ別のセルと、同じセルの続きを見分けるために持つ。
        IsContinuation: bool
    }

/// 表を区切り行で分けた部分。
type Part =
    /// 表を横断する見出しセルだけの行。小見出し。
    | Caption of Inline list
    /// 表を横断する通常セルだけの行。自由記述。
    | Note of Block list
    /// 列を持つ行の連なり。結合セルは展開済み。
    | Rows of GridCell array list

/// 文字も画像も入れ子も持たないマスか。
let isBlankCell (cell: GridCell) : bool =
    String.IsNullOrEmpty cell.Text
    && not cell.HasImage
    && List.isEmpty cell.Content.Blocks

/// 中身からマスを作る。
let private cellOf (isHeader: bool) (content: CellContent) : GridCell =
    { IsHeader = isHeader
      Content = content
      Text = (plainText content.Inlines).Trim()
      HasImage = hasImage content.Inlines
      IsContinuation = false }

/// 中身を持たないマス。
let private emptyCell (isHeader: bool) : GridCell =
    cellOf isHeader { Inlines = []; Blocks = [] }

/// 改行の境界をどう繋ぐか決める。
/// 前が句読点や開き括弧などで終わるか、後が区切り記号や閉じ括弧などで始まるなら、
/// そのまま詰めても読めるのでNoneを返す。どちらでもなければ挟む読点を返す。
/// 対象が日本語のwikiであることを前提にした結合規則。
let private joinSeparator (previousText: string) (nextText: string) : string option =
    let flushBefore = "。．！？!?…、,/／・:：(（「『"
    let flushAfter = "/／・、。,)）」』(（"

    let flushable =
        previousText <> "" && flushBefore.Contains previousText[previousText.Length - 1]
        || nextText <> "" && flushAfter.Contains nextText[0]

    if flushable then None else Some "、"

/// 並びを改行で行へ分ける。中身の無い行は落とす。
let private splitLines (inlines: Inline list) : Inline list list =
    let lines, last =
        inlines
        |> List.fold
            (fun (lines, current) inlineNode ->
                match inlineNode with
                | LineBreak -> List.rev current :: lines, []
                | _ -> lines, inlineNode :: current)
            ([], [])

    List.rev (List.rev last :: lines) |> List.filter (isBlank >> not)

/// 行の並びを1行へ繋ぐ。
/// 見出しセルは文ではなくラベルで、改行は表示幅の都合の折り返しでしかないので、区切りを挟まず詰める。
/// それ以外は、境界の前後の要素の文字を見て、詰めるか読点で繋ぐ。
/// 画像は文字を持たないので、画像との境界は読点で繋がる。
let joinLines (isHeader: bool) (lines: Inline list list) : Inline list =
    match lines with
    | [] -> []
    | first :: rest ->
        rest
        |> List.fold
            (fun joined next ->
                let previousText =
                    match List.tryLast joined with
                    | Some last -> (plainText [ last ]).Trim()
                    | None -> ""

                let nextText =
                    match next with
                    | head :: _ -> (plainText [ head ]).Trim()
                    | [] -> ""

                if isHeader then
                    joined @ next
                else
                    match joinSeparator previousText nextText with
                    | None -> joined @ next
                    | Some separator -> joined @ Text separator :: next)
            first

/// セルの中身を、1行へ繋いだ文字と入れ子のブロックへ分ける。
/// 段落の切れ目も改行も行の境界として同じ規則で繋ぐ。
/// セルの中の改行は表示幅のための折り返しで、文の途中にも入るためである。
let private flatten (isHeader: bool) (blocks: Block list) : CellContent =
    let lines =
        blocks
        |> List.collect (fun block ->
            match block with
            | Paragraph inlines -> splitLines inlines
            | _ -> [])

    let nested =
        blocks
        |> List.filter (fun block ->
            match block with
            | Paragraph _ -> false
            | _ -> not (isBlankBlock block))

    { Inlines = joinLines isHeader lines
      Blocks = nested }

/// colspanの切り詰め上限。
/// rowspan/colspanは外部HTML由来の未検証値で、HTML仕様上は65534と1000まで指定できる。
/// そのまま使うと悪意ある値や編集ミスで格子が数千万要素へ膨らむため、
/// rowspanは実際の残り行数で、colspanは現実のwikiテーブルを大きく超えるこの定数で切り詰める。
let private maxColumnSpan = 100

/// 文書全体で共有する格子の総エントリ数の既定の予算。
/// 1セルあたりの切り詰めだけでは総量が行数×1行のセル数×rowSpan×colSpanで二次的に膨らむため、
/// 見積りが残り予算を超える表は展開を諦める。現実のwikiのページを大きく超える値。
let defaultBudget = 1_000_000L

/// セルが格子で占める行方向と列方向の広がり。
/// HTML仕様のrowspan="0"は「セクションの最後まで」を意味するため、残り行数へ展開する。
let private cellSpans (rowCount: int) (rowIndex: int) (cell: Cell) : int * int =
    let rowSpan =
        if cell.RowSpan <= 0 then
            rowCount - rowIndex
        else
            min cell.RowSpan (rowCount - rowIndex)

    rowSpan, max 1 (min cell.ColumnSpan maxColumnSpan)

/// rowspan/colspanを考慮した格子を組み立て、結合セルを個別のマスへ複製展開する。
/// 縦方向の複製は文字だけを引き継ぐ。行の見出しとして意味があるためで、画像は先頭の行にだけ残す。
/// 横方向の複製は空にする。同じ行を読めば分かる繰り返しでしかないため。
/// 全てのマスが空になった行は取り除く。
/// 展開した格子と、消費したエントリ数を返す。
/// 総エントリ数の見積りが残り予算budgetを超える表は、
/// メモリ枯渇やハングを防ぐため結合を展開せず、1セルを1マスとして読む。
let private expand (budget: int64) (rows: Cell list list) : GridCell array list * int64 =
    let rows = rows |> List.map Array.ofList |> Array.ofList

    let cells =
        rows
        |> Array.map (
            Array.map (fun cell -> cellOf cell.IsHeader (flatten cell.IsHeader cell.Content))
        )

    let estimatedEntries =
        rows
        |> Array.mapi (fun rowIndex row ->
            row
            |> Array.sumBy (fun cell ->
                let rowSpan, columnSpan = cellSpans rows.Length rowIndex cell
                int64 rowSpan * int64 columnSpan))
        |> Array.sum

    let grid, consumed =
        if budget < estimatedEntries then
            cells, 0L
        else
            let grid = Array.init rows.Length (fun _ -> ResizeArray<GridCell voption>())

            let setEntry
                (rowEntries: ResizeArray<GridCell voption>)
                (index: int)
                (entry: GridCell)
                =
                while rowEntries.Count <= index do
                    rowEntries.Add ValueNone

                rowEntries[index] <- ValueSome entry

            rows
            |> Array.iteri (fun rowIndex row ->
                let rowEntries = grid[rowIndex]
                let mutable columnIndex = 0

                row
                |> Array.iteri (fun cellIndex cell ->
                    while columnIndex < rowEntries.Count && rowEntries[columnIndex].IsSome do
                        columnIndex <- columnIndex + 1

                    let origin = cells[rowIndex][cellIndex]
                    let rowSpan, columnSpan = cellSpans rows.Length rowIndex cell

                    for i in 0 .. rowSpan - 1 do
                        for j in 0 .. columnSpan - 1 do
                            let entry =
                                if i = 0 && j = 0 then
                                    origin
                                elif j = 0 && not (String.IsNullOrEmpty origin.Text) then
                                    cellOf
                                        origin.IsHeader
                                        { Inlines = [ Text origin.Text ]
                                          Blocks = [] }
                                else
                                    emptyCell origin.IsHeader

                            setEntry
                                grid[rowIndex + i]
                                (columnIndex + j)
                                { entry with IsContinuation = 0 < i }

                    columnIndex <- columnIndex + columnSpan))

            grid
            |> Array.map (fun rowEntries ->
                rowEntries |> Seq.choose ValueOption.toOption |> Seq.toArray),
            estimatedEntries

    grid |> Array.filter (Array.forall isBlankCell >> not) |> Array.toList, consumed

/// 画像だけの行と、その真下の文字だけの行を1行へ畳む。
/// 必要素材や愛用品の強化の表は、素材の画像の行と個数の行を縦に重ね、
/// Lvのような行の見出しを縦の結合で両方の行へ掛けている。
/// そのまま1行1項目にすると、同じLvの項目が画像だけと個数だけの2つに分かれて、
/// どの素材が何個なのかを項目をまたいで数えないと読めない。
/// 畳むのは、列ごとに見て以下のどれかだけで出来ている2行に限る。
///
/// - 上が画像だけで下が文字だけ。画像の後ろへ文字を続ける
/// - 下が上からの縦の結合の複製。上を残す
/// - 下が空。上を残す
///
/// 縦の結合で繋がった2行であることを、文字を持つ複製の列が1つはあることで確かめる。
/// 無関係な2行がたまたま画像の行と文字の行だっただけの時に、項目を混ぜないためである。
/// 文字が同じことでは確かめない。
/// 対象が1人だけのボーナスの表は、その1人の行と合計の行の数値が全て同じになる。
let private foldImageRows (rows: GridCell array list) : GridCell array list =
    let isImageOnly (cell: GridCell) : bool =
        cell.HasImage && String.IsNullOrEmpty cell.Text

    let isTextOnly (cell: GridCell) : bool =
        not cell.HasImage
        && not (String.IsNullOrEmpty cell.Text)
        && List.isEmpty cell.Content.Blocks

    let isPair (upper: GridCell, lower: GridCell) : bool =
        isImageOnly upper && isTextOnly lower && not lower.IsContinuation

    let tryFold (upper: GridCell array) (lower: GridCell array) : GridCell array option =
        let columns = Array.zip upper lower

        if
            upper.Length = lower.Length
            && Array.exists isPair columns
            && lower
               |> Array.exists (fun cell ->
                   cell.IsContinuation && not (String.IsNullOrEmpty cell.Text))
            && columns
               |> Array.forall (fun (upperCell, lowerCell) ->
                   isPair (upperCell, lowerCell)
                   || lowerCell.IsContinuation
                   || isBlankCell lowerCell)
        then
            columns
            |> Array.map (fun (upperCell, lowerCell) ->
                if isPair (upperCell, lowerCell) then
                    cellOf
                        upperCell.IsHeader
                        { upperCell.Content with
                            Inlines =
                                upperCell.Content.Inlines @ Text " " :: lowerCell.Content.Inlines }
                else
                    upperCell)
            |> Some
        else
            None

    let rec fold (rows: GridCell array list) : GridCell array list =
        match rows with
        | upper :: lower :: rest ->
            match tryFold upper lower with
            | Some folded -> folded :: fold rest
            | None -> upper :: fold (lower :: rest)
        | _ -> rows

    fold rows

/// 見出しの下が全て空になった列を列ごと取り除く。
/// 空の列はノイズでしかないため。
/// ただし2行の表では空セルがデータの欠けを意味しうるので、
/// 見出しに中身のある列は3行以上の表でだけ取り除く。
/// 先頭行を見出し行として扱えるのは全セルが見出しセルの場合だけで、
/// データ行から始まる表で先頭行にだけ値がある列を消すと事実データが落ちる。
let private dropEmptyColumns (rows: GridCell array list) : GridCell array list =
    match rows with
    | first :: (_ :: _ as body) ->
        let hasHeaderRow = first |> Array.forall _.IsHeader
        let columnCount = rows |> List.map Array.length |> List.max

        // 行によってセル数が違うため、短い行では列が無いことを空として扱う。
        let isEmptyAt (index: int) (row: GridCell array) : bool =
            index >= row.Length || isBlankCell row[index]

        let dropped =
            seq { 0 .. columnCount - 1 }
            |> Seq.filter (fun index ->
                List.forall (isEmptyAt index) body
                && (isEmptyAt index first || hasHeaderRow && 3 <= rows.Length))
            |> Set.ofSeq

        rows
        |> List.map (fun row ->
            row
            |> Array.indexed
            |> Array.filter (fun (index, _) -> not (dropped.Contains index))
            |> Array.map snd)
    | _ -> rows

/// キーと値のペアを横に繰り返すレイアウトの行(th,td,th,td,...)を1行1ペアの2列へ正規化する。
/// 列の意味が揃わずデータとして混乱するため。
/// 行末尾の空セルは幅合わせの埋め草なので、ペアの判定から除いて捨てる。
/// 中身が両方空のペアも行にしない。
let private normalizePairsRows (rows: GridCell array list) : GridCell array list =
    rows
    |> List.collect (fun row ->
        let pairLength =
            match Array.tryFindIndexBack (isBlankCell >> not) row with
            | None -> 0
            | Some lastIndex -> lastIndex + 1

        let pairCells = Array.sub row 0 pairLength

        let isPairsRow =
            4 <= pairCells.Length
            && pairCells.Length % 2 = 0
            && pairCells
               |> Array.indexed
               |> Array.forall (fun (index, cell) -> cell.IsHeader = (index % 2 = 0))

        if isPairsRow then
            pairCells
            |> Array.chunkBySize 2
            |> Array.filter (Array.forall isBlankCell >> not)
            |> Array.toList
        else
            [ row ])

/// 表全体を横断する1セルだけの行か。小見出しや自由記述の行。
/// 1セルだけに見える行には上からのrowspanで埋まる継続行もあるため、
/// colspanで横に広がっている行だけを区切り行とみなす。
let private isDivider (row: Cell list) : bool =
    match row with
    | [ cell ] -> 1 < cell.ColumnSpan
    | _ -> false

/// 表を、区切り行の小見出しと自由記述と、その間の列を持つ行の格子へ分ける。
/// 区切り行を列を持つ行と同じ一覧に混ぜると、長い記述が1項目として他の行に並んでしまうため。
/// 全行が1セルの表は1列の表として意味を持つのでそのまま格子にする。
/// 残り予算budgetから消費したエントリ数も返す。
let parts (budget: int64) (table: Table) : Part list * int64 =
    let splits =
        List.exists isDivider table.Rows && not (List.forall isDivider table.Rows)

    /// 溜めた行を格子にして部分へ加える。
    let flush (pending: Cell list list) (parts: Part list, consumed: int64) : Part list * int64 =
        match pending with
        | [] -> parts, consumed
        | _ ->
            let grid, used = expand (budget - consumed) (List.rev pending)

            match grid |> foldImageRows |> dropEmptyColumns |> normalizePairsRows with
            | [] -> parts, consumed + used
            | rows -> Rows rows :: parts, consumed + used

    let state, pending =
        table.Rows
        |> List.fold
            (fun (state, pending) row ->
                match row with
                | [ cell ] when splits && isDivider row ->
                    let content = flatten cell.IsHeader cell.Content
                    let parts, consumed = flush pending state

                    let divider =
                        if isBlankContent content then
                            []
                        elif cell.IsHeader then
                            [ Caption content.Inlines ]
                        else
                            [ Note(
                                  (if isBlank content.Inlines then
                                       []
                                   else
                                       [ Paragraph content.Inlines ])
                                  @ content.Blocks
                              ) ]

                    (divider @ parts, consumed), []
                | _ -> state, row :: pending)
            (([], 0L), [])

    let parts, consumed = flush pending state
    List.rev parts, consumed
