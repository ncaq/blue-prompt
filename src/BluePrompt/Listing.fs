/// 表を、ラベル付きの項目の並びへ読み解く。
///
/// GFMのパイプテーブルは整形で列幅を揃えるため、
/// 画像のURLや長いセリフのような広いセルが1つあるだけで全ての行が空白で埋まり、
/// Open WebUIのKnowledgeではチャンクの大半が空白になる。
/// また表の行は列の意味を見出し行に預けているので、
/// チャンクの境界で見出し行から切り離されると数値が何なのか分からなくなる。
/// 箇条書きなら1項目ごとに「列名: 値」とラベルを抱えるため、どこで切られても読める。
/// 生成物へ整形を掛けない例外を作るのではなく、整形しても膨らまない形を出力して解決する。
///
/// 表の見出し行は、列名の行とその上の題名の行に分ける。
/// 題名はスキルの表の「Q.E.D」のように表が何の一覧かを言う行で、
/// 書き出す時に、表の属する節の1つ下の見出しになる。
/// 題名が複数並ぶ表は題名ごとの組へ分け、どの題名にも属さないキーの列を各組で繰り返す。
module BluePrompt.Listing

open System
open BluePrompt.Article
open BluePrompt.Grid

/// 文字を持たないマスか。画像だけのマスも含む。
let private isTextless (cell: GridCell) : bool = String.IsNullOrEmpty cell.Text

/// 文字を持つ見出しセルか。
let private isHeaderText (cell: GridCell) : bool = cell.IsHeader && not (isTextless cell)

/// 行のindex列のマス。行によってセル数が違うため、短い行では列が無いことをNoneで表す。
let private tryCell (row: GridCell array) (index: int) : GridCell option =
    if index < row.Length then Some row[index] else None

/// キーの列。文字を持つ見出しセルがあり、中身を持つ通常セルが1つも無い列。
/// プロフィールの項目名や固有武器の★のように、行ごとのキーを見出しセルで書く列で、
/// ここにある見出しセルは行の見出しであって、その行が見出し行であることを意味しない。
let private keyColumns (columnCount: int) (rows: GridCell array list) : Set<int> =
    seq { 0 .. columnCount - 1 }
    |> Seq.filter (fun index ->
        let cells = rows |> List.choose (fun row -> tryCell row index)

        List.exists isHeaderText cells
        && cells |> List.forall (fun cell -> cell.IsHeader || isBlankCell cell))
    |> Set.ofSeq

/// 見出し行か。キーの列を除いて、文字を持つ見出しセルが文字を持つ通常セルより多い行。
/// スキルの表はアイコンの通常セルと名前の見出しセルが並ぶ行で始まり、
/// 絆ランクボーナス合計の表は画像の列の見出しを通常セルで書くため、全てが見出しセルであることは求めない。
/// 画像だけのセルはどちらにも数えない。
/// ちびキャラのように、キーの見出しセルと画像だけの値のセルしか持たない行を見出し行にしないため。
let private isHeaderRow (keys: Set<int>) (row: GridCell array) : bool =
    let votes =
        row
        |> Array.indexed
        |> Array.choose (fun (index, cell) ->
            if keys.Contains index || isTextless cell then
                None
            else
                Some cell)

    let headerVotes = votes |> Array.filter _.IsHeader |> Array.length
    votes.Length - headerVotes < headerVotes

/// 見出し行の列ごとのラベル。見出しセルの文字だけを読み、通常セルは見出しとして扱わない。
let private rowLabels (columnCount: int) (row: GridCell array) : string array =
    Array.init columnCount (fun index ->
        match tryCell row index with
        | Some cell when isHeaderText cell -> cell.Text
        | _ -> "")

/// 題名行の列ごとのラベル。
/// 横に結合された題名は展開で左端にだけ文字が残るため、空のセルには左の題名を引き継ぐ。
/// 最初の題名より左の列は、どの題名にも属さないキーの列として空のまま残す。
/// 題名行のアイコンは名前に添えた飾りで、文字を持たないので何も残さない。
let private titleLabels (columnCount: int) (row: GridCell array) : string array =
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
    (titleRows: GridCell array list)
    : string array =
    titleRows
    |> List.map (titleLabels columnCount)
    |> List.fold
        (Array.map2 (fun upper lower ->
            [ upper; lower ]
            |> List.filter (String.IsNullOrEmpty >> not)
            |> String.concat " "))
        (Array.create columnCount "")
    |> Array.map2 (fun label title -> if title = label then "" else title) labels

/// 立ち絵のように、縦に結合された画像のセル1つで埋まっている列。
/// 結合セルの展開は画像を先頭の行にだけ残し、続く行には文字だけの複製を置くため、
/// 中身のあるセルのうち先頭だけが画像を持ち、残りが先頭と同じ文字で画像を持たない列がそれにあたる。
/// 画像だけのセルの複製は文字も画像も持たない空のセルになるので、複製は空のセルも数える。
/// 複製が1つも無ければ結合されていたセルではないので、
/// データ行が1行だけの表の画像は立ち絵ではない。
/// 行の項目へ混ぜると名前の値へ画像が割り込むため、列ごと取り出して独立した項目にする。
/// 素材のアイコンのように文字の行と並ぶ画像は、その行の値として残る。
let private liftedImageColumns (columnCount: int) (dataRows: GridCell array list) : Set<int> =
    seq { 0 .. columnCount - 1 }
    |> Seq.filter (fun index ->
        match
            dataRows
            |> List.choose (fun row -> tryCell row index)
            |> List.skipWhile isBlankCell
        with
        | origin :: (_ :: _ as copies) ->
            origin.HasImage
            && copies |> List.forall (fun copy -> not copy.HasImage && copy.Text = origin.Text)
        | _ -> false)
    |> Set.ofSeq

/// データ行のうちcolumnsの列を1つの項目にする。
/// 文字を持つ見出しセルはキーで、それ以外は列名の行のラベルを添えた値になる。
/// キーも列名の行も無い表(ボイスのラベルとセリフのような2列)では、
/// 先頭のセルが文字を持てばそれをキーとして読む。先頭が画像だけなら後ろは探さない。
/// 中身のある部品が無い行は項目にしない。
let private rowItem
    (labels: string array)
    (hasLabelRow: bool)
    (columns: int list)
    (row: GridCell array)
    : Article.Part list option =
    let parts =
        columns
        |> List.choose (fun index ->
            match tryCell row index with
            | Some cell when not (isBlankCell cell) ->
                if isHeaderText cell then
                    Some(Key cell.Content, cell)
                else
                    Some(Value(labels[index], cell.Content), cell)
            | _ -> None)

    let hasKey =
        parts
        |> List.exists (fun (part, _) ->
            match part with
            | Key _ -> true
            | Value _ -> false)

    let parts =
        match parts with
        | (Value(_, content), cell) :: rest when
            not hasKey && not hasLabelRow && not (isTextless cell)
            ->
            (Key content, cell) :: rest
        | _ -> parts

    match parts with
    | [] -> None
    | _ -> Some(List.map fst parts)

/// 表の行を、見出し行の連なりとそれに続くデータ行の区間へ分ける。
/// 固有武器の表のように、データ行の後に列名の行が現れて別の一覧が続く表があるため。
/// データ行で始まる表は、見出し行を持たない区間から始まる。
let rec private segments
    (keys: Set<int>)
    (rows: GridCell array list)
    : (GridCell array list * GridCell array list) list =
    match rows with
    | [] -> []
    | _ ->
        let headerRows = List.takeWhile (isHeaderRow keys) rows
        let rest = List.skip headerRows.Length rows
        let dataRows = rest |> List.takeWhile (isHeaderRow keys >> not)
        (headerRows, dataRows) :: segments keys (List.skip dataRows.Length rest)

/// 1つの区間を、題名ごとの項目の組にする。
/// 見出し行のうち最後を列名の行、それより上を題名の行として読む。
let private segmentGroups
    (columnCount: int)
    (headerRows: GridCell array list, dataRows: GridCell array list)
    : ItemGroup list =
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
            |> List.tryPick (fun row -> tryCell row index |> Option.filter (isBlankCell >> not))
            |> Option.map (fun cell -> [ Value(labels[index], cell.Content) ]))

    /// columnsの列から組み立てた項目。valueColumnsが全て空の行は項目にしない。
    let itemsOf (columns: int list) (valueColumns: int list) : Article.Part list list =
        let itemColumns = columns |> List.filter (lifted.Contains >> not)

        dataRows
        |> List.filter (fun row ->
            valueColumns
            |> List.exists (fun index ->
                match tryCell row index with
                | Some cell -> not (isBlankCell cell)
                | None -> false))
        |> List.choose (rowItem labels labelRow.IsSome itemColumns)

    let allColumns = [ 0 .. columnCount - 1 ]

    let groups =
        groupLabels
        |> Array.filter (String.IsNullOrEmpty >> not)
        |> Array.distinct
        |> Array.toList

    match groups with
    | [] ->
        [ { Title = None
            Items = liftedItems @ itemsOf allColumns allColumns } ]
    | _ ->
        groups
        |> List.mapi (fun groupIndex group ->
            let groupColumns =
                allColumns |> List.filter (fun index -> groupLabels[index] = group)

            let columns =
                allColumns
                |> List.filter (fun index ->
                    String.IsNullOrEmpty groupLabels[index] || groupLabels[index] = group)

            { Title = Some group
              Items = (if groupIndex = 0 then liftedItems else []) @ itemsOf columns groupColumns })

/// 格子の行を、区間ごとの項目のブロックにする。
/// データ行が1つも無い表は、見出し行をそのままデータ行として読む。
let private rowsBlocks (rows: GridCell array list) : Block list =
    let columnCount = rows |> List.map Array.length |> List.fold max 0

    let dataSegments =
        segments (keyColumns columnCount rows) rows
        |> List.filter (fun (_, dataRows) -> not (List.isEmpty dataRows))

    let segmentsToRead =
        match dataSegments with
        | [] -> [ [], rows ]
        | _ -> dataSegments

    segmentsToRead
    |> List.map (segmentGroups columnCount)
    |> List.map (List.filter (fun group -> not (List.isEmpty group.Items) || group.Title.IsSome))
    |> List.filter (List.isEmpty >> not)
    |> List.map Items

/// ブロックの並びの中の表を全て、小見出しと項目の並びへ読み解く。
/// 表のセルや自由記述の中の入れ子の表も読み解く。
/// 格子の予算は文書全体で持ち回る。
/// 上限を1つの表ごとに掛けると、文書全体では上限×表の数まで膨らむため。
let analyze (blocks: Block list) : Block list =
    // 残り予算は表から表へ引き継ぐ状態で、入れ子の表を読む再帰からも減らすため、参照セルで持つ。
    let remaining = ref defaultBudget

    let rec analyzeBlocks (blocks: Block list) : Block list =
        collectBlocks
            (fun block ->
                match block with
                | Table table ->
                    let tableParts, consumed = parts remaining.Value table
                    remaining.Value <- remaining.Value - consumed

                    tableParts
                    |> List.collect (fun part ->
                        match part with
                        | Caption inlines -> [ Article.Caption inlines ]
                        | Note inner -> analyzeBlocks inner
                        | Rows rows -> rowsBlocks rows |> List.map analyzeNested)
                | _ -> [ block ])
            blocks

    /// 項目の中身に入れ子になった表を読み解く。
    and analyzeNested (block: Block) : Block =
        match block with
        | Items groups ->
            let content (cellContent: CellContent) : CellContent =
                { cellContent with
                    Blocks = analyzeBlocks cellContent.Blocks }

            groups
            |> List.map (fun group ->
                { group with
                    Items =
                        group.Items
                        |> List.map (
                            List.map (fun part ->
                                match part with
                                | Key cellContent -> Key(content cellContent)
                                | Value(label, cellContent) -> Value(label, content cellContent))
                        ) })
            |> Items
        | _ -> block

    analyzeBlocks blocks
