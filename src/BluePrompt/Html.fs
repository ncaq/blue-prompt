/// AngleSharpのDOMを記事の木へ解釈する。
/// 汎用のHTMLの読み方だけを持ち、サイト固有の決まりは呼び出し側が渡す。
/// DOMを読むのはここまでで、以降の変換は記事の木の上で行う。
module BluePrompt.Html

open System
open AngleSharp.Dom
open AngleSharp.Html.Dom
open BluePrompt.Article

/// 呼び出し側が決める読み方。
type Options =
    {
        /// 要素をサイト固有の決まりで行の中の要素として読む。読まない要素にはNoneを返す。
        /// 脚注の参照のように、HTMLとしてはただのリンクでも意味の違う要素に使う。
        Inline: IElement -> Inline option
    }

/// サイト固有の読み方を持たない設定。
let plain: Options = { Inline = fun _ -> None }

/// 表示対象ではない要素。中身ごと読み飛ばす。
/// タグを落としても文字として本文へ混ざると、スクリプト片やフォームの部品が成果物へ紛れ込む。
let private skippedTags =
    Set.ofList
        [ "script"
          "style"
          "template"
          "noscript"
          "head"
          "link"
          "meta"
          "svg"
          "input"
          "select"
          "textarea"
          "button"
          "iframe"
          "object"
          "embed"
          "audio"
          "canvas" ]

/// 段落を切る要素。これらの前後で、文字とインライン要素の並びが1つの段落として閉じる。
/// 一覧に無い要素(spanなど)は切らずに透過する。
let private blockTags =
    Set.ofList
        [ "p"
          "div"
          "h1"
          "h2"
          "h3"
          "h4"
          "h5"
          "h6"
          "ul"
          "ol"
          "li"
          "dl"
          "dt"
          "dd"
          "table"
          "thead"
          "tbody"
          "tfoot"
          "tr"
          "td"
          "th"
          "blockquote"
          "pre"
          "hr"
          "section"
          "article"
          "main"
          "center"
          "figure"
          "figcaption"
          "details"
          "summary"
          "address"
          "header"
          "footer"
          "nav"
          "aside"
          "form" ]

/// 段落を切る要素のセレクタ。インライン要素が中に段落を切る要素を含むかの判定に使う。
let private blockSelector = String.concat ", " blockTags

/// 見出し要素のセレクタ。見出しを含む表はデータの表ではなくレイアウトとして読む。
let private headingSelector = "h1, h2, h3, h4, h5, h6"

/// 文書のURLを基準にhttp/httpsの絶対URLへ解決する。
/// 文字列からパースした文書のURLはabout:blankで、相対参照はhttp/httpsへ解決されずNoneになる。
let private resolveWebUri (node: INode) (reference: string) : Uri option =
    match Uri.TryCreate(node.BaseUri, UriKind.Absolute) with
    | true, baseUri ->
        match Uri.TryCreate(baseUri, reference.Trim()) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
            Some uri
        | _ -> None
    | _ -> None

/// 画像の実体のURLの参照。
/// 遅延読み込みの画像はsrcにプレースホルダのdata URIを置き、本物をdata-srcに持つ。
let private imageReference (image: IElement) : string option =
    [ image.GetAttribute "data-src"; image.GetAttribute "src" ]
    |> List.tryFind (fun reference ->
        not (String.IsNullOrWhiteSpace reference)
        && not (reference.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)))

/// 動画の実体のURLの参照。srcが無ければ子のsourceのsrcを使う。
let private videoReference (video: IElement) : string option =
    video.GetAttribute "src"
    :: (video.QuerySelectorAll "source"
        |> Seq.map (fun source -> source.GetAttribute "src")
        |> Seq.toList)
    |> List.tryFind (String.IsNullOrWhiteSpace >> not)

/// HTMLが1つの空白として扱う文字か。
/// HTMLの規則ではASCIIの空白だけが潰れるが、
/// ノーブレークスペースも幅を取るための詰め物としてしか現れないので、同じく空白として潰す。
/// 全角空白は本文の文字なので潰さない。
let private isCollapsible (c: char) : bool =
    c = ' ' || c = '\t' || c = '\n' || c = '\r' || c = '\f' || c = ' '

/// 空白の連なりを1つの空白へ潰す。
let private collapseSpaces (text: string) : string =
    let builder = Text.StringBuilder()

    for c in text do
        if isCollapsible c then
            if builder.Length = 0 || builder[builder.Length - 1] <> ' ' then
                builder.Append ' ' |> ignore
        else
            builder.Append c |> ignore

    builder.ToString()

/// 隣り合う文字を1つへまとめ、まとめた後に重なった空白を潰す。空の文字は落とす。
let private mergeText (inlines: Inline list) : Inline list =
    List.foldBack
        (fun inlineNode merged ->
            match inlineNode, merged with
            | Text Str.NullOrEmpty, _ -> merged
            | Text left, Text right :: rest -> Text(collapseSpaces (left + right)) :: rest
            // 隣り合う同じ強調は1つへまとめる。別々に書くと記号が連なって強調として読めなくなる。
            | Strong left, Strong right :: rest -> Strong(left @ right) :: rest
            | Emphasis left, Emphasis right :: rest -> Emphasis(left @ right) :: rest
            | Strikethrough left, Strikethrough right :: rest ->
                Strikethrough(left @ right) :: rest
            | _ -> inlineNode :: merged)
        inlines
        []

/// 並びの両端の空白と改行を落とす。改行の前後の空白も落とす。
let private trimEdges (inlines: Inline list) : Inline list =
    /// 先頭の空白と改行を落とす。
    let rec trimStart (inlines: Inline list) : Inline list =
        match inlines with
        | LineBreak :: rest -> trimStart rest
        | Text text :: rest ->
            match text.TrimStart ' ' with
            | Str.NullOrEmpty -> trimStart rest
            | Str.NonEmpty trimmed -> Text trimmed :: rest
        | _ -> inlines

    /// 改行の直後の空白を落とす。
    let rec afterBreaks (inlines: Inline list) : Inline list =
        match inlines with
        | LineBreak :: Text text :: rest ->
            match text.TrimStart ' ' with
            | Str.NullOrEmpty -> afterBreaks (LineBreak :: rest)
            | Str.NonEmpty trimmed -> LineBreak :: afterBreaks (Text trimmed :: rest)
        | head :: rest -> head :: afterBreaks rest
        | [] -> []

    /// 向きを変えて同じ規則を掛けるために、文字の空白の落とす側を入れ替える。
    let flip (inlines: Inline list) : Inline list =
        inlines
        |> List.rev
        |> List.map (fun inlineNode ->
            match inlineNode with
            | Text text -> Text(String(Array.rev (text.ToCharArray())))
            | _ -> inlineNode)

    inlines |> trimStart |> afterBreaks |> flip |> trimStart |> afterBreaks |> flip

/// 並びの空白を整える。
/// 強調やリンクの端の空白は外へ出す。
/// 端に空白を抱えた強調はMarkdownでは強調として読まれず、区切りの前にも空白が残るため。
/// 中身を失った強調は落とす。リンクは中身が無くても参照先を持つので残す。
let rec private tidy (inlines: Inline list) : Inline list =
    /// 入れ物の中身を整えて、端の空白を外へ出す。
    let wrap (rebuild: Inline list -> Inline) (keepEmpty: bool) (content: Inline list) =
        let inner = tidy content

        let startsWithSpace =
            match inner with
            | Text text :: _ -> text.StartsWith ' '
            | _ -> false

        let endsWithSpace =
            match List.tryLast inner with
            | Some(Text text) -> text.EndsWith ' '
            | _ -> false

        let trimmed = trimEdges inner

        (if startsWithSpace then [ Text " " ] else [])
        @ (if List.isEmpty trimmed && not keepEmpty then
               []
           else
               [ rebuild trimmed ])
        @ (if endsWithSpace then [ Text " " ] else [])

    inlines
    |> List.collect (fun inlineNode ->
        match inlineNode with
        | Text text -> [ Text(collapseSpaces text) ]
        | Strong content -> wrap Strong false content
        | Emphasis content -> wrap Emphasis false content
        | Strikethrough content -> wrap Strikethrough false content
        | Link(target, content) -> wrap (fun inner -> Link(target, inner)) true content
        | Ruby _
        | Image _
        | FootnoteReference _
        | LineBreak -> [ inlineNode ])
    |> mergeText

/// 読んだ並びを、空白を整えて両端を落とした形にする。
let private normalize (inlines: Inline list) : Inline list = trimEdges (tidy inlines)

/// 同じリンク先をまたぐ改行を繋ぐ。
/// 長いリンクの表示名を同じリンク先の複数のaへ分けて折り返す書き方があり、
/// その境界はひと続きの表示名の途中なので、区切りを挟んではいけない。
/// 表のセルを読む時にだけ掛ける。セルの改行は表示幅のための折り返しだからである。
let rec private foldSameLinks (inlines: Inline list) : Inline list =
    match inlines with
    | Link(left, leftContent) :: LineBreak :: Link(right, rightContent) :: rest when left = right ->
        foldSameLinks (Link(left, leftContent @ rightContent) :: rest)
    | head :: rest -> head :: foldSameLinks rest
    | [] -> []

/// 段落を切る要素として読むか。
/// インライン要素でも、中に段落を切る要素を含むなら入れ物として読む。
/// リンクや色付けのspanが表や段落を包んでいることがあるため。
let private isBlockLike (element: IElement) : bool =
    blockTags.Contains element.LocalName
    || not (isNull (element.QuerySelector blockSelector))

/// 要素の子を、行の中の要素の並びとして読む。整える前の並びを返す。
let rec private readInlines (options: Options) (node: INode) : Inline list =
    node.ChildNodes |> Seq.toList |> List.collect (readInline options)

/// ノード1つを、行の中の要素として読む。
and private readInline (options: Options) (node: INode) : Inline list =
    match node with
    | :? IElement as element ->
        match options.Inline element with
        | Some inlineNode -> [ inlineNode ]
        | None ->
            match element.LocalName with
            | name when skippedTags.Contains name -> []
            | "br" -> [ LineBreak ]
            | "img" ->
                // http/httpsの絶対URLへ解決できない画像は辿れないので落とす。
                match imageReference element |> Option.bind (resolveWebUri element) with
                | Some source ->
                    let alt =
                        match element.GetAttribute "alt" with
                        | Str.NullOrEmpty -> ""
                        | Str.NonEmpty alt -> alt

                    [ Image(alt, source) ]
                | None -> []
            | "video" ->
                // Markdownは動画を埋め込めず、videoの中身は再生できない環境向けの定型文でしかないため、
                // URLを表示文字にしたリンクにする。
                match videoReference element |> Option.bind (resolveWebUri element) with
                | Some source -> [ Link(source, [ Text source.AbsoluteUri ]) ]
                | None -> []
            | "a" ->
                let content = readInlines options element

                match element.GetAttribute "href" with
                | null -> content
                | href ->
                    match resolveWebUri element href with
                    | Some target -> [ Link(target, content) ]
                    | None -> content
            | "strong"
            | "b" -> [ Strong(readInlines options element) ]
            | "em"
            | "i" -> [ Emphasis(readInlines options element) ]
            | "del"
            | "s"
            | "strike" -> [ Strikethrough(readInlines options element) ]
            | "ruby" ->
                /// 読みの要素(rtとrp)か。
                let isReading (child: INode) : bool =
                    match child with
                    | :? IElement as childElement ->
                        childElement.LocalName = "rt" || childElement.LocalName = "rp"
                    | _ -> false

                let text =
                    element.ChildNodes
                    |> Seq.filter (isReading >> not)
                    |> Seq.map _.TextContent
                    |> String.concat ""

                let reading =
                    element.QuerySelectorAll "rt" |> Seq.map _.TextContent |> String.concat ""

                [ Ruby(text.Trim(), reading.Trim()) ]
            | _ -> readInlines options element
    | _ when node.NodeType = NodeType.Text -> [ Text node.TextContent ]
    | _ -> []

/// ノードの並びをブロックの並びとして読む。
/// 段落を切る要素の間に現れた文字とインライン要素の並びを、1つの段落にまとめる。
let rec private readBlocks (options: Options) (nodes: INode list) : Block list =
    /// 溜めた並びを段落として閉じる。中身が無ければ何も足さない。
    let flush (pending: Inline list) (blocks: Block list list) : Block list list =
        /// 文字を持たなくても意味を持つ要素か。
        /// 脚注の参照と、埋め込みのツイートのような中身の無いリンクがあたる。
        /// リンクは参照先を持つので、表示文字をどうするかは後の段が決める。
        let isSignificant (inlineNode: Inline) : bool =
            match inlineNode with
            | FootnoteReference _
            | Link _ -> true
            | _ -> false

        match normalize (List.rev pending) with
        | inlines when isBlank inlines && not (List.exists isSignificant inlines) -> blocks
        | inlines -> [ Paragraph inlines ] :: blocks

    let pending, blocks =
        nodes
        |> List.fold
            (fun (pending, blocks) node ->
                match node with
                | :? IElement as element when skippedTags.Contains element.LocalName ->
                    pending, blocks
                | :? IElement as element when
                    Option.isNone (options.Inline element) && isBlockLike element
                    ->
                    [], readBlock options element :: flush pending blocks
                | _ -> List.rev (readInline options node) @ pending, blocks)
            ([], [])

    flush pending blocks |> List.rev |> List.concat

/// 段落を切る要素1つをブロックの並びとして読む。
and private readBlock (options: Options) (element: IElement) : Block list =
    let children () : Block list =
        readBlocks options (Seq.toList element.ChildNodes)

    match element.LocalName with
    | "h1"
    | "h2"
    | "h3"
    | "h4"
    | "h5"
    | "h6" ->
        match normalize (readInlines options element) with
        | content when isBlank content -> []
        | content -> [ Heading(int (element.LocalName.Substring 1), content) ]
    | "ul"
    | "ol" ->
        let items =
            element.Children
            |> Seq.filter (fun child -> child.LocalName = "li")
            |> Seq.map (fun item -> readBlocks options (Seq.toList item.ChildNodes))
            |> Seq.filter (List.forall isBlankBlock >> not)
            |> Seq.toList

        match items with
        | [] -> []
        | _ when element.LocalName = "ol" -> [ OrderedList items ]
        | _ -> [ BulletList items ]
    | "table" ->
        match element with
        | :? IHtmlTableElement as table when isNull (table.QuerySelector headingSelector) ->
            let rows =
                table.Rows
                |> Seq.map (fun row ->
                    row.Cells
                    |> Seq.map (fun cell ->
                        { IsHeader = cell.LocalName = "th"
                          RowSpan = cell.RowSpan
                          ColumnSpan = cell.ColumnSpan
                          Content =
                            readBlocks options (Seq.toList cell.ChildNodes)
                            |> mapInlines foldSameLinks })
                    |> Seq.toList)
                |> Seq.toList

            [ Table { Rows = rows } ]
        // 見出しを含む表はデータの表ではなくレイアウトなので、セルの中身を順に並べて読む。
        | _ -> children ()
    | "blockquote" ->
        match children () with
        | [] -> []
        | blocks -> [ Quote blocks ]
    | "pre" ->
        match element.TextContent with
        | text when String.IsNullOrWhiteSpace text -> []
        | text -> [ CodeBlock(text.Trim('\r', '\n')) ]
    | "hr" -> [ ThematicBreak ]
    | "dl" ->
        // 用語の行に説明を続けた段落として読む。
        // Markdownには定義リストが無いので、用語と説明が並んで読める形にする。
        element.Children
        |> Seq.toList
        |> List.collect (fun child ->
            match child.LocalName with
            | "dt" -> [ Paragraph(normalize (readInlines options child)) ]
            | _ -> readBlocks options (Seq.toList child.ChildNodes))
        |> List.fold
            (fun merged block ->
                match merged, block with
                | Paragraph term :: rest, Paragraph description when
                    not (isBlank term) && List.forall ((<>) LineBreak) term
                    ->
                    Paragraph(term @ LineBreak :: description) :: rest
                | _ -> block :: merged)
            []
        |> List.rev
        |> List.filter (isBlankBlock >> not)
    | _ -> children ()

/// 要素の子をブロックの並びとして読む。
let blocks (options: Options) (root: INode) : Block list =
    readBlocks options (Seq.toList root.ChildNodes)

/// ノードの並びを、行の中の要素の並びとして読む。脚注の定義のように、ブロックを成さない並びに使う。
let inlines (options: Options) (nodes: INode seq) : Inline list =
    nodes |> Seq.toList |> List.collect (readInline options) |> normalize
