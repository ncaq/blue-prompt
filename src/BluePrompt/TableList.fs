/// 平坦化したデータの表を箇条書きへ置き換えるDOM操作。
///
/// GFMのパイプテーブルは整形で列幅を揃えるため、
/// 画像のURLや長いセリフのような広いセルが1つあるだけで全ての行が空白で埋まり、
/// Open WebUIのKnowledgeではチャンクの大半が空白になる。
/// また表の行は列の意味を見出し行に預けているので、
/// チャンクの境界で見出し行から切り離されると数値が何なのか分からなくなる。
/// 箇条書きなら1項目ごとに「列名: 値」とラベルを抱えるため、どこで切られても読める。
///
/// 表の見出し行は、列名の行とその上の題名の行に分ける。
/// 題名はスキルの表の「Q.E.D」のように表が何の一覧かを言う行で、
/// 表の属する節の1つ下の見出しにする。
/// 題名が複数並ぶ表は題名ごとの節へ分け、どの題名にも属さないキーの列を各節で繰り返す。
///
/// 見出しを含む表はデータではなくレイアウトなので触らない。
/// 結合セルが残る表は平坦化を諦めた過大な表なので、同じくそのまま残す。
module BluePrompt.TableList

open System
open AngleSharp.Dom
open AngleSharp.Html.Dom

/// 見出し要素のセレクタ。
let private headingSelector = "h1, h2, h3, h4, h5, h6"

/// 見出しの深さの上限。HTMLの見出しはh6まで。
let private maxHeadingLevel = 6

/// 読み取ったセル。
/// TextContentはアクセスごとに部分木を走査して文字列を生成するため、一度だけ読んで持ち回る。
type private Cell =
    {
        Element: IHtmlTableCellElement
        /// トリム済みの文字。
        Text: string
        /// 見出しセル(th)か。
        IsHeader: bool
        /// 画像を含むか。文字を持たず画像だけのセルも値として意味を持つ。
        HasImage: bool
    }

let private readCell (cell: IHtmlTableCellElement) : Cell =
    { Element = cell
      Text = cell.TextContent.Trim()
      IsHeader = cell.TagName = "TH"
      HasImage = not (isNull (cell.QuerySelector "img")) }

/// 文字を持たないセルか。画像だけのセルも含む。
let private isTextless (cell: Cell) : bool = String.IsNullOrEmpty cell.Text

/// 文字も画像も持たないセルか。
let private isBlank (cell: Cell) : bool = isTextless cell && not cell.HasImage

/// 文字を持つ見出しセルか。
let private isHeaderText (cell: Cell) : bool = cell.IsHeader && not (isTextless cell)

/// 行のindex列のセル。行によってセル数が違うため、短い行では列が無いことをNoneで表す。
let private tryCell (row: Cell array) (index: int) : Cell option =
    if index < row.Length then Some row[index] else None

/// キーの列。文字を持つthがあり、中身を持つtdが1つも無い列。
/// プロフィールの項目名や固有武器の★のように、行ごとのキーをthで書く列で、
/// ここにあるthは行の見出しであって、その行が見出し行であることを意味しない。
let private keyColumns (columnCount: int) (rows: Cell array list) : Set<int> =
    seq { 0 .. columnCount - 1 }
    |> Seq.filter (fun index ->
        let cells = rows |> List.choose (fun row -> tryCell row index)

        List.exists isHeaderText cells
        && cells |> List.forall (fun cell -> cell.IsHeader || isBlank cell))
    |> Set.ofSeq

/// 見出し行か。キーの列を除いて、文字を持つthが文字を持つtdより多い行。
/// スキルの表はアイコンのtdと名前のthが並ぶ行で始まり、
/// 絆ランクボーナス合計の表は画像の列の見出しをtdで書くため、全てがthであることは求めない。
/// 画像だけのセルはどちらにも数えない。
/// ちびキャラのように、キーのthと画像だけの値のtdしか持たない行を見出し行にしないため。
let private isHeaderRow (keys: Set<int>) (row: Cell array) : bool =
    let votes =
        row
        |> Array.indexed
        |> Array.filter (fun (index, cell) -> not (keys.Contains index) && not (isTextless cell))
        |> Array.map snd

    let headerVotes = votes |> Array.filter (fun cell -> cell.IsHeader) |> Array.length
    votes.Length - headerVotes < headerVotes

/// 見出し行の列ごとのラベル。thの文字だけを読み、tdは見出しとして扱わない。
let private rowLabels (columnCount: int) (row: Cell array) : string array =
    Array.init columnCount (fun index ->
        match tryCell row index with
        | Some cell when isHeaderText cell -> cell.Text
        | _ -> "")

/// 題名行の列ごとのラベル。
/// 横に結合された題名は展開で左端にだけ文字が残るため、空のセルには左の題名を引き継ぐ。
/// 最初の題名より左の列は、どの題名にも属さないキーの列として空のまま残す。
/// 題名行のアイコンは名前に添えた飾りで、文字を持たないので何も残さない。
let private titleLabels (columnCount: int) (row: Cell array) : string array =
    rowLabels columnCount row
    |> Array.mapFold
        (fun carried label ->
            let label = if String.IsNullOrEmpty label then carried else label
            label, label)
        ""
    |> fst

/// 複数の題名行を列ごとに空白で繋いだラベル。題名行が無ければ全ての列が空になる。
/// 縦に結合された見出しは展開で列名の行にも同じ文字が複製されるため、
/// 列名と同じ題名はその列の題名ではなく、キーの列として空にする。
let private combinedTitleLabels
    (columnCount: int)
    (labels: string array)
    (titleRows: Cell array list)
    : string array =
    titleRows
    |> List.map (titleLabels columnCount)
    |> List.fold
        (Array.map2 (fun upper lower ->
            [ upper; lower ]
            |> List.filter (fun label -> not (String.IsNullOrEmpty label))
            |> String.concat " "))
        (Array.create columnCount "")
    |> Array.map2 (fun label title -> if title = label then "" else title) labels

/// 立ち絵のように、縦に結合された画像のセル1つで埋まっている列。
/// 結合セルの展開は画像を先頭の行にだけ残し、続く行には文字だけの複製を置くため、
/// 中身のあるセルのうち先頭だけが画像を持ち、残りが先頭と同じ文字で画像を持たない列がそれにあたる。
/// 行の項目へ混ぜると名前の値へ画像が割り込むため、列ごと取り出して独立した項目にする。
/// 素材のアイコンのように文字の行と並ぶ画像は、その行の値として残る。
let private liftedImageColumns (columnCount: int) (dataRows: Cell array list) : Set<int> =
    seq { 0 .. columnCount - 1 }
    |> Seq.filter (fun index ->
        match
            dataRows
            |> List.choose (fun row -> tryCell row index)
            |> List.filter (fun cell -> not (isBlank cell))
        with
        | origin :: copies ->
            origin.HasImage
            && copies |> List.forall (fun copy -> not copy.HasImage && copy.Text = origin.Text)
        | [] -> false)
    |> Set.ofSeq

/// 項目を組み立てる部品。見出しセルはキーで、それ以外は列名を添えた値。
type private Part =
    | Key of Cell
    | Value of string * Cell

/// 空白だけのテキストノードか。
let private isWhitespaceNode (node: INode) : bool =
    node.NodeType = NodeType.Text && String.IsNullOrWhiteSpace node.TextContent

/// セルの中身の複製。
/// リンクや画像を保つため、文字ではなく子ノードごと複製する。
/// キーの列は題名ごとの節で繰り返し使うため、移すと2つ目の節から空になる。
/// 両端の空白だけのノードは、後ろに繋ぐ区切りの前へ空白が残るので落とす。
let private contentOf (cell: Cell) : INode array =
    cell.Element.ChildNodes
    |> Seq.skipWhile isWhitespaceNode
    |> Seq.rev
    |> Seq.skipWhile isWhitespaceNode
    |> Seq.rev
    |> Seq.map (fun node -> node.Clone true)
    |> Seq.toArray

/// 部品を並べて項目の要素にする。キーの後は「: 」で、値の後は「, 」で繋ぐ。
let private renderItem (document: IDocument) (parts: Part list) : IElement =
    let item = document.CreateElement "li"

    let separators =
        parts
        |> List.pairwise
        |> List.map (fun (previous, _) ->
            match previous with
            | Key _ -> ": "
            | Value _ -> ", ")

    List.zip (None :: List.map Some separators) parts
    |> List.iter (fun (separator, part) ->
        separator
        |> Option.iter (fun text -> item.Append [| document.CreateTextNode text :> INode |])

        match part with
        | Key cell -> item.Append(contentOf cell)
        | Value(label, cell) ->
            if not (String.IsNullOrEmpty label) then
                item.Append [| document.CreateTextNode(label + ": ") :> INode |]

            item.Append(contentOf cell))

    item

/// データ行のうちcolumnsの列を1つの項目にする。
/// 文字を持つthはキーで、それ以外は列名の行のラベルを添えた値になる。
/// キーも列名の行も無い表(ボイスのラベルとセリフのような2列)では、
/// 先頭の文字を持つセルをキーとして読む。
/// 中身のある部品が無い行は項目にしない。
let private rowItem
    (document: IDocument)
    (labels: string array)
    (hasLabelRow: bool)
    (columns: int list)
    (row: Cell array)
    : IElement option =
    let parts =
        columns
        |> List.choose (fun index ->
            match tryCell row index with
            | Some cell when not (isBlank cell) ->
                if isHeaderText cell then
                    Some(Key cell)
                else
                    Some(Value(labels[index], cell))
            | _ -> None)

    let hasKey =
        parts
        |> List.exists (fun part ->
            match part with
            | Key _ -> true
            | Value _ -> false)

    let parts =
        match parts with
        | Value(_, cell) :: rest when not hasKey && not hasLabelRow && not (isTextless cell) ->
            Key cell :: rest
        | _ -> parts

    match parts with
    | [] -> None
    | _ -> Some(renderItem document parts)

/// 題名の要素。見出しの深さを使い切った時は、区切り行と同じく強調で見出しらしさを残す。
let private titleElement (document: IDocument) (level: int) (text: string) : IElement =
    if level <= maxHeadingLevel then
        let heading = document.CreateElement $"h%d{level}"
        heading.TextContent <- text
        heading
    else
        let paragraph = document.CreateElement "p"
        let emphasis = document.CreateElement "strong"
        emphasis.TextContent <- text
        paragraph.Append [| emphasis :> INode |]
        paragraph

/// 表の行を、見出し行の連なりとそれに続くデータ行の区間へ分ける。
/// 固有武器の表のように、データ行の後に列名の行が現れて別の一覧が続く表があるため。
/// データ行で始まる表は、見出し行を持たない区間から始まる。
let rec private segments
    (keys: Set<int>)
    (rows: Cell array list)
    : (Cell array list * Cell array list) list =
    match rows with
    | [] -> []
    | _ ->
        let headerRows = List.takeWhile (isHeaderRow keys) rows
        let rest = List.skip headerRows.Length rows
        let dataRows = rest |> List.takeWhile (fun row -> not (isHeaderRow keys row))
        (headerRows, dataRows) :: segments keys (List.skip dataRows.Length rest)

/// 1つの区間を題名の見出しと箇条書きの並びにする。
/// 見出し行のうち最後を列名の行、それより上を題名の行として読む。
let private segmentNodes
    (document: IDocument)
    (titleLevel: int)
    (columnCount: int)
    (headerRows: Cell array list, dataRows: Cell array list)
    : INode list =
    let labelRow, titleRows =
        match List.rev headerRows with
        | [] -> None, []
        | last :: rest -> Some last, List.rev rest

    let labels =
        match labelRow with
        | Some row -> rowLabels columnCount row
        | None -> Array.create columnCount ""

    let groupLabels = combinedTitleLabels columnCount labels titleRows
    let lifted = liftedImageColumns columnCount dataRows

    // 立ち絵の列の項目。区間の先頭に1回だけ置く。
    let liftedItems =
        lifted
        |> Set.toList
        |> List.choose (fun index ->
            dataRows
            |> List.tryPick (fun row ->
                tryCell row index |> Option.filter (fun cell -> not (isBlank cell)))
            |> Option.map (fun cell -> renderItem document [ Value(labels[index], cell) ]))

    /// columnsの列から組み立てた箇条書き。valueColumnsが全て空の行は項目にしない。
    let listOf (leading: IElement list) (columns: int list) (valueColumns: int list) : INode list =
        let itemColumns = columns |> List.filter (fun index -> not (lifted.Contains index))

        let items =
            dataRows
            |> List.filter (fun row ->
                valueColumns
                |> List.exists (fun index ->
                    match tryCell row index with
                    | Some cell -> not (isBlank cell)
                    | None -> false))
            |> List.choose (rowItem document labels labelRow.IsSome itemColumns)

        match leading @ items with
        | [] -> []
        | items ->
            let list = document.CreateElement "ul"
            list.Append(items |> List.map (fun item -> item :> INode) |> List.toArray)
            [ list ]

    let allColumns = [ 0 .. columnCount - 1 ]

    let groups =
        groupLabels
        |> Array.filter (fun label -> not (String.IsNullOrEmpty label))
        |> Array.distinct
        |> Array.toList

    match groups with
    | [] -> listOf liftedItems allColumns allColumns
    | _ ->
        groups
        |> List.mapi (fun groupIndex group ->
            let groupColumns =
                allColumns |> List.filter (fun index -> groupLabels[index] = group)

            let columns =
                allColumns
                |> List.filter (fun index ->
                    String.IsNullOrEmpty groupLabels[index] || groupLabels[index] = group)

            let leading = if groupIndex = 0 then liftedItems else []

            (titleElement document titleLevel group :> INode)
            :: listOf leading columns groupColumns)
        |> List.concat

/// 1つの表を箇条書きへ置き換える。titleLevelは題名の見出しの深さ。
/// データ行が1つも無い表は置き換えない。
let private convertTable (document: IDocument) (titleLevel: int) (table: IHtmlTableElement) : unit =
    let rows =
        table.Rows
        |> Seq.map (fun row -> row.Cells |> Seq.map readCell |> Seq.toArray)
        |> Seq.toList

    let columnCount = rows |> List.map Array.length |> List.fold max 0

    let dataSegments =
        segments (keyColumns columnCount rows) rows
        |> List.filter (fun (_, dataRows) -> not (List.isEmpty dataRows))

    if not (List.isEmpty dataSegments) then
        let replacement =
            dataSegments |> List.collect (segmentNodes document titleLevel columnCount)

        table.Replace(List.toArray replacement)

/// データの表か。見出しを含む表はレイアウトで、結合セルが残る表は平坦化を諦めた表なので除く。
let private isDataTable (table: IHtmlTableElement) : bool =
    isNull (table.QuerySelector headingSelector)
    && isNull (table.QuerySelector "[rowspan], [colspan]")

/// 文書中のデータの表を全て箇条書きへ置き換える。
/// 題名の見出しは表の直前にある見出しの1つ下の深さにする。見出しが無ければh2になる。
/// 内側の表を先に置き換えないと外側のセルの中身を複製する時に表のままになるため、文書の逆順に走査する。
let convert (document: IDocument) : unit =
    document.QuerySelectorAll $"%s{headingSelector}, table"
    |> Seq.toArray
    |> Array.fold
        (fun (level, tables) element ->
            match element with
            | :? IHtmlTableElement as table -> level, (table, level + 1) :: tables
            | heading -> int (heading.LocalName.Substring 1), tables)
        (1, [])
    |> snd
    |> List.iter (fun (table, titleLevel) ->
        if isDataTable table then
            convertTable document titleLevel table)
