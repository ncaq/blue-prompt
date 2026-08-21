/// wikiruの学校別キャラクター一覧の構造化。
/// 「どの学校のどの部活に誰が居るか」をレコードの列へパースし、
/// LLM向けのMarkdownをそこから生成する。
/// このページは生徒1人を1つのテーブルとして並べたカードの敷き詰めで、
/// pandocへそのまま流すと1行1列のテーブルが人数分並ぶだけの読めない一覧になるため、
/// pandocを通さずDOMから直接読む。
/// パースはHTML文字列だけで完結するため、ネットワークを使わずに単体テストで検証できる。
module BluePrompt.School

open System
open AngleSharp.Dom
open AngleSharp.Html.Parser

/// 生徒1人分の所属。
type Entry =
    {
        /// 所属する学校。ページのh2見出し。
        /// 「所属不明・所属なし」や「コラボキャラクター」のような、
        /// 学校ではない区分の見出しもここに入る。
        School: string
        /// 所属する部活・組織。ページのh3見出し。
        /// 連邦生徒会のように学校直下へ生徒が並ぶ場合はNone。
        Club: string option
        /// レアリティ。★3・★2・★1と、プレイアブルではない生徒を表すNPCが入る。
        Rarity: string
        /// 生徒の名前。
        /// リンク先のページ名ではなくカードの表示名を採る。
        /// カイテンジャーの5人は全員が同じページの別の節へリンクしていて、
        /// ページ名では互いを区別できないため。
        Name: string
        /// 名前と食い違う場合の、リンク先のwikiruのページ名。
        /// 「カイテンレッド」に対する「カイテンジャー」のように、
        /// 表示名のままでは個別ページを引けない生徒が居る。
        /// 名前と同じ場合とリンクが無い場合はNone。
        Page: string option
    }

/// 一覧から1件も生徒を得られなかった時。
/// サイト側の構造変更でパースが全滅した可能性が高く、空のデータによる上書きを防ぐ。
exception EntryNotFound

/// 生徒のセルからレアリティと名前の組を読めなかった時のセルのテキスト。
exception CellShapeError of text: string

/// ノードの列のテキストを繋いで前後の空白を落とす。
/// カードの名前は末尾へ`&nbsp;`が付くが、
/// .NETはノーブレークスペースも空白として扱うのでTrimで一緒に落ちる。
let private nodesText (nodes: INode list) : string =
    nodes |> List.map _.TextContent |> String.concat "" |> _.Trim()

/// 生徒1人分のセルを「レアリティと名前」の組へ分解する。
/// セルはレアリティ・アイコン画像・名前をbrで縦へ並べたカードなので、
/// 最初のbrより前をレアリティ、それより後ろのテキストを名前として読む。
/// 画像はテキストを持たないため名前には混ざらない。
/// どちらかが空になるセルはカードとして読めていないので、
/// 黙って空の行を混ぜずCellShapeErrorを送出する。
let private parseCell (cell: IElement) : string * string =
    let isBreak (node: INode) =
        match node with
        | :? IElement as element -> element.LocalName = "br"
        | _ -> false

    let nodes = List.ofSeq cell.ChildNodes

    let rarity, name =
        match nodes |> List.skipWhile (isBreak >> not) with
        | [] -> nodesText nodes, ""
        | _ :: afterBreak ->
            nodesText (nodes |> List.takeWhile (isBreak >> not)), nodesText afterBreak

    if String.IsNullOrEmpty rarity || String.IsNullOrEmpty name then
        raise (CellShapeError(text = cell.TextContent.Trim()))

    rarity, name

/// セルのリンク先からwikiruのページ名を取り出す。
/// カードのリンクは「./?<パーセントエンコードされたページ名>」の相対リンクで、
/// 節へのリンクは末尾へ「#<節のid>」が付く。
/// 他のページの記法で書かれたリンクはページ名を復元できないのでNoneにする。
let private pageName (cell: IElement) : string option =
    cell.QuerySelectorAll "a"
    |> Seq.tryPick (fun anchor ->
        match anchor.GetAttribute "href" with
        | null -> None
        | href when href.StartsWith("./?", StringComparison.Ordinal) ->
            match Uri.UnescapeDataString(href.Substring 3).Split '#' with
            | [||] -> None
            | parts -> Some parts[0]
        | _ -> None)

/// パース済みDOMから一覧の全レコードを取り出す。
/// 本文の見出し(h2学校 > h3部活)を状態として辿り、
/// その配下に並ぶ生徒のカードをレコード化する。
/// 学校の見出しより前にあるページ上部のナビゲーションのセルは、
/// 学校が決まっていないので自然に読み飛ばされる。
/// 1件も得られなかった場合はEntryNotFoundを送出する。
let parse (document: IDocument) : Entry list =
    // 累積へ後ろから連結するとセルごとに累積全体のコピーが走るため、
    // 先頭へ積んで最後に反転することでO(レコード数)に保つ。
    let entries =
        document.QuerySelectorAll "#body h2, #body h3, #body td"
        |> Seq.fold
            (fun (entries, (school, club)) element ->
                match element.LocalName with
                | "h2" -> entries, (Some(Extract.headingText element), None)
                | "h3" -> entries, (school, Some(Extract.headingText element))
                | _ ->
                    match school with
                    | Some school ->
                        let rarity, name = parseCell element

                        { School = school
                          Club = club
                          Rarity = rarity
                          Name = name
                          Page = pageName element |> Option.filter (fun page -> page <> name) }
                        :: entries,
                        (Some school, club)
                    | None -> entries, (school, club))
            ([], (None, None))
        |> fst
        |> List.rev

    if List.isEmpty entries then
        raise EntryNotFound

    entries

/// HTML文字列から一覧の全レコードを取り出す。
/// パースしてparseへ委譲する。挙動と失敗条件はそちらと同じ。
let parseHtml (html: string) : Entry list =
    use document = HtmlParser().ParseDocument html
    parse document

/// レコードの列からLLM参照用のMarkdown本文を組み立てる。
/// 学校(h2)だけを見出しに残し、部活はテーブルの列へ移す。
/// ページと同じ部活の見出しの階層にすると1つ数人のテーブルが並んで、
/// Open WebUIのKnowledgeが見出しで割る断片も細切れになるため、
/// 学校ごとの1つのテーブルへまとめて、行だけで所属が分かるようにする。
/// 名前と食い違うページ名は、生徒個別ページを引く手掛かりとして名前の後ろへ添える。
let toReferenceMarkdown (entries: Entry list) : string =
    let schoolBlock (school: string, schoolEntries: Entry list) =
        let rows =
            schoolEntries
            |> List.map (fun entry ->
                let club = Option.defaultValue "" entry.Club

                let name =
                    match entry.Page with
                    | Some page -> $"%s{entry.Name}(ページ名: %s{page})"
                    | None -> entry.Name

                $"| %s{Markdown.escapeTableCell club} "
                + $"| %s{Markdown.escapeTableCell entry.Rarity} "
                + $"| %s{Markdown.escapeTableCell name} |")

        [ $"## %s{school}"; ""; "| 部活・組織 | レアリティ | 生徒 |"; "| --- | --- | --- |" ]
        @ rows
        @ [ "" ]

    entries
    |> List.groupBy _.School
    |> List.collect schoolBlock
    |> String.concat "\n"
