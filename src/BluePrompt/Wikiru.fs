/// ブルーアーカイブ攻略有志Wiki(bluearchive.wikiru.jp)固有のページ取得とナレッジ化。
/// 取得したDOMを記事の木へ読み、節の後始末と選別を木の上で済ませて、最後にMarkdownへ書き出す。
module BluePrompt.Wikiru

open System
open System.IO
open System.Threading.Tasks
open AngleSharp.Dom
open XParsec
open XParsec.Parsers
open XParsec.CharParsers
open BluePrompt.Article

/// wikiruのページ名から記事URLを組み立てる。
let pageUri (pageName: string) : Uri =
    Uri $"https://bluearchive.wikiru.jp/?%s{Uri.EscapeDataString pageName}"

/// 記事の本文(#body)が無かった時の取得元のURL。
/// サイト側の構造変更で本文を見失った可能性が高く、空のナレッジによる上書きを防ぐために止める。
exception BodyNotFound of url: string

/// 生徒個別ページからナレッジとして残すセクションの見出し。
/// ゲーム内外の事実を載せているセクションだけを列挙するホワイトリスト。
/// 「小ネタ」はゲーム内外の事実のまとめで、事実には著作権が発生しないため、
/// 他の事実セクションと同じ扱いで残す(plugins/jp-wikiru-bluearchive/README.md)。
/// 「ゲームにおいて」「運用考察」などのwiki独自の解説・考察は
/// 著作権方針によりそのままの形では置かないため、
/// 列挙されていないセクションは落ちる。
let studentSectionTitles: string list =
    [ "基本情報"; "スキル"; "固有武器"; "愛用品"; "能力解放"; "絆ランクボーナス"; "絆ストーリー"; "ボイス"; "小ネタ" ]

/// 生徒個別ページからrole-playスキルの参照として残すセクションの見出し。
/// 人格と話し方を示す基本情報とボイスに、
/// 衣装ごとの出来事や人柄を伝える小ネタを加えて残し、
/// 性能データの参照はjp-wikiru-bluearchive側のスキルに任せる。
/// 絆ストーリーはタイトルと報酬の表、メモリアルロビーはBGMとスタッフの表しか持たず、
/// タイトルだけでは中身の作り話を誘うため外している。
let rolePlaySectionTitles: string list = [ "基本情報"; "ボイス"; "小ネタ" ]

/// プレイアブルではない登場人物のページからナレッジとして残すセクションの見出し。
/// 生徒個別ページと同じ形のページが多いので生徒のホワイトリストを土台にして、
/// 生徒のページには無い事実のセクションだけを足す。
/// アロナとプラナとソラはゲーム内のボイスを「ボイス集」の節に持つ。
/// カイテンジャーは5人で1ページで、メンバーごとのプロフィールと紹介文がそれぞれ1つの節になっている。
/// 「公式ツイッターの紹介」は埋め込みのツイートだけで、抽出すると中身が残らないため含めない。
let unplayableSectionTitles: string list =
    studentSectionTitles
    @ [ "ボイス集"; "カイテンレッド"; "カイテンブラック"; "カイテングリーン"; "カイテンイエロー"; "カイテンピンク" ]

/// 脚注のアンカーからラベルを読む。脚注のアンカーでなければNone。
/// wikiruの脚注は本文中の参照も#noteの定義も「*1」の文字を持つa.note_superで、
/// 番号をラベルにする。
/// Markdownへ書いた「*1」を読み直す作りだと、直後に続く数字まで番号として吸い込むため、
/// 要素として読める時点でラベルを確定させる。
let private footnoteLabel (element: IElement) : string option =
    if element.LocalName = "a" && element.ClassList.Contains "note_super" then
        match element.TextContent.Trim().TrimStart '*' with
        | Str.NullOrEmpty -> None
        | Str.NonEmpty label -> Some label
    else
        None

/// タブの入れ物を、ラベルと中身の組の並びとして読む。タブの入れ物でなければNone。
/// wikiruのタブは、idが「tabs」で始まるdivの直下に、
/// ラベルのリンクを並べたulと、リンクの先のidを持つ中身のdivを置いている。
/// そのまま読むと、ラベルの箇条書きの後ろへ全てのタブの中身が区切り無く続いて、
/// どこからがどのタブの中身なのかが分からなくなる。
let private tabs (element: IElement) (read: INode list -> Block list) : Block list option =
    let isContainer =
        element.LocalName = "div"
        && not (isNull element.Id)
        && element.Id.StartsWith("tabs", StringComparison.Ordinal)

    let panels =
        if isContainer then
            element.Children
            |> Seq.filter (fun child -> child.LocalName = "ul")
            |> Seq.collect _.QuerySelectorAll("li > a[href^='#']")
            |> Seq.choose (fun anchor ->
                let target = (anchor.GetAttribute "href").TrimStart '#'

                element.Children
                |> Seq.tryFind (fun child -> child.Id = target)
                |> Option.map (fun panel ->
                    [ Text(anchor.TextContent.Trim()) ], read (Seq.toList panel.ChildNodes)))
            |> Seq.toList
        else
            []

    match panels with
    | [] -> None
    | _ -> Some [ Tabs panels ]

/// wikiruの記事を読む時の、サイト固有の読み方。
let private htmlOptions: Html.Options =
    { Inline = fun element -> footnoteLabel element |> Option.map FootnoteReference
      Block = tabs }

/// 脚注の定義を#noteから読む。
/// wikiruの脚注定義は「アンカー、本文のspan、brの繰り返し」の並びなので、
/// アンカーから次のbrまでの兄弟ノードを本文として読む。
/// 区切りのbrが欠けたマークアップでも次の脚注を巻き込まないように、
/// 次の脚注アンカーでも読み終える。
let private readFootnotes (document: IDocument) : Footnote list =
    document.QuerySelectorAll "#note a.note_super"
    |> Seq.choose (fun anchor ->
        footnoteLabel anchor
        |> Option.map (fun label ->
            let nodes =
                (anchor :> INode)
                |> Seq.unfold (fun node ->
                    match node.NextSibling with
                    | null -> None
                    | :? IElement as element when element.LocalName = "br" -> None
                    | :? IElement as element when element.ClassList.Contains "note_super" -> None
                    | next -> Some(next, next))
                |> Seq.toList

            { Label = label
              Content = Html.inlines htmlOptions nodes }))
    |> Seq.filter (fun footnote -> not (isBlank footnote.Content))
    |> List.ofSeq

/// リンクを整理する。
///
/// - 文書と同じホストへのリンクは外して中身だけ残す。
///   サイト内の他ページへのリンクは数が多く本文を埋めてしまう。
///   他のサイトへのリンクは出典として辿れるように残す
/// - 同じURLの画像1つだけを包むリンクは外す。画像を原寸で開くためのリンクで、URLを繰り返すだけになる
/// - 埋め込みのツイートのように中身を持たないリンクは、何も指せない空のリンクになるため、URLを表示文字にする
let rec private tidyLinks (host: string) (inlines: Inline list) : Inline list =
    inlines
    |> List.collect (fun inlineNode ->
        match inlineNode with
        | Link(target, content) ->
            let content = tidyLinks host content

            if target.Host = host then
                content
            else
                match content with
                | [ Image(_, source) ] when source = target -> content
                | _ when isBlank content -> [ Link(target, [ Text target.AbsoluteUri ]) ]
                | _ -> [ Link(target, content) ]
        | Strong content -> [ Strong(tidyLinks host content) ]
        | Emphasis content -> [ Emphasis(tidyLinks host content) ]
        | Strikethrough content -> [ Strikethrough(tidyLinks host content) ]
        | Text _
        | Ruby _
        | Image _
        | FootnoteReference _
        | LineBreak -> [ inlineNode ])

/// ノイズを除いたDOMから、記事を記事の木として読む。
/// 本文(#body)をブロックとして、脚注(#note)を脚注の定義として読み、
/// サイトのヘッダ・サイドバー・フッタは読まない。
/// 脚注の無いページには#noteが無いので、#noteが無いことは正常として扱う。
/// 本文が無ければBodyNotFoundを送出する。
let readArticle (document: IDocument) : Article =
    match document.QuerySelector "#body" with
    | null -> raise (BodyNotFound document.Url)
    | body ->
        let host =
            match Uri.TryCreate(document.BaseUri, UriKind.Absolute) with
            | true, uri -> uri.Host
            | _ -> ""

        { Blocks = Html.blocks htmlOptions body |> mapInlines (tidyLinks host)
          Footnotes =
            readFootnotes document
            |> List.map (fun footnote ->
                { footnote with
                    Content = tidyLinks host footnote.Content }) }

/// 編集者向けの欄の案内だけの行か。
/// ページのテンプレートが置いていく「〜を書く欄です。」は、
/// 記事を書く人へ向けた欄の案内で、生徒についての事実ではない。
/// 行全体が案内であることを求めるため、手前に別の文を持つ行は残す。
let private isEditorNotice (line: string) : bool =
    /// 案内の文の中の1文字。句点で文が切れる。
    let withinSentence: Parse.TextParser<char> = satisfy (fun c -> c <> '。')

    Parse.startsWith (manyCharsTill withinSentence (pstring "を書く欄です。") .>> eof) line

/// 区切り文字だけの行か。
/// リンクを「/」で並べたナビゲーションは、並べた要素が落ちると区切り文字だけの行になる。
let private isSeparatorRemnant (line: string) : bool =
    Parse.startsWith (skipMany1 (anyOf "/、" >>. Parse.inlineSpaces) .>> eof) line

/// 立ち絵に添えられた、全体画像へのリンクの案内。
/// 元のページでは縮小画像を包む全体画像へのリンクの説明で、リンクを外した後は辿る先が無い。
let private fullImageCaption = "クリックで全体画像"

/// 本文ではない行を落とす。行は改行で区切る。
/// 表を読み解く前に掛けることで、落とした行を繋いだ読点が跡として残らないようにする。
let private dropNoiseLines (inlines: Inline list) : Inline list =
    let lines, last =
        inlines
        |> List.fold
            (fun (lines, current) inlineNode ->
                match inlineNode with
                | LineBreak -> List.rev current :: lines, []
                | _ -> lines, inlineNode :: current)
            ([], [])

    let isNoise (line: Inline list) : bool =
        let text = (plainText line).Trim()

        not (hasImage line)
        && (text = fullImageCaption || isEditorNotice text || isSeparatorRemnant text)

    match List.rev (List.rev last :: lines) |> List.filter (isNoise >> not) with
    | [] -> []
    | kept -> List.reduce (fun upper lower -> upper @ LineBreak :: lower) kept

/// 段落を、2つ以上続いた改行の所で分ける。
/// wikiruの本文は段落の要素を使わず、改行1つで行を、改行2つで段落を区切って書かれている。
/// 1つだけの改行は行の区切りとして段落の中へ残す。
/// 表のセルの中の改行は表示幅のための折り返しなので、ここへ来る前に読点の規則で繋がれている。
let private splitParagraphs (blocks: Block list) : Block list =
    /// 改行の連なりで区切った行のまとまり。それぞれが1つの段落になる。
    let split (inlines: Inline list) : Inline list list =
        let paragraphs, current, _ =
            inlines
            |> List.fold
                (fun (paragraphs, current, breaks) inlineNode ->
                    match inlineNode with
                    | LineBreak -> paragraphs, current, breaks + 1
                    | _ when breaks >= 2 -> List.rev current :: paragraphs, [ inlineNode ], 0
                    | _ when breaks = 1 && not (List.isEmpty current) ->
                        paragraphs, inlineNode :: LineBreak :: current, 0
                    | _ -> paragraphs, inlineNode :: current, 0)
                ([], [], 0)

        List.rev (List.rev current :: paragraphs)

    blocks
    |> collectBlocks (fun block ->
        match block with
        | Paragraph inlines ->
            split inlines
            |> List.filter (fun line ->
                not (isBlank line) || not (Set.isEmpty (footnoteLabels [ Paragraph line ])))
            |> List.map Paragraph
        | _ -> [ block ])

/// 節の見出しの文字。
let private titleText (section: Section) : string = (plainText section.Title).Trim()

/// コメント欄の節か。
/// コメントそのものは後から読み込まれるので取得したHTMLには残らないが、
/// 投稿のルールを畳んで置いているページがあり、
/// 見出しだけを消すとルールの本文が直前の節の続きとして残ってしまうため、節ごと落とす。
let private isCommentSection (section: Section) : bool =
    let title = titleText section
    title = "コメント" || title = "コメントフォーム"

/// 読んだ記事を、ナレッジとして読める形へ整える。
/// 何をどの順で施すかは以下の並びがそのまま示している。
///
/// - 本文ではない行を落とすのは表を読み解く前。読点の跡を残さないため
/// - 表を読み解くのは節の後始末より前。中身を失った節は、表を読み解いた後でないと決まらないため
/// - 最初の見出しより前は切り落とす。wikiruの記事は本文が最初の見出しから始まり、
///   それより前は関連ページへのナビゲーションや注意書きで、ナレッジには要らない。
///   見出しが無い文書はそのまま残す
let prepare (article: Article) : Article =
    let read =
        article.Blocks
        |> mapInlines dropNoiseLines
        |> Listing.analyze
        |> splitParagraphs
        |> outline

    let kept =
        read.Sections |> filterSections (isCommentSection >> not) |> dropEmptySections

    { article with
        Blocks =
            ofOutline
                { Preamble = if List.isEmpty read.Sections then read.Preamble else []
                  Sections = kept } }

/// 節を分けている見出しの深さ。wikiruの記事はh2で節を分ける。
let private sectionLevel = 2

/// h2見出しのホワイトリストで節を選別する。
/// h2の見出しがtitlesに載っている節だけを、h3以下の小見出しごと残す。
let selectSections (titles: string list) (article: Article) : Article =
    let read = outline article.Blocks

    { article with
        Blocks =
            ofOutline
                { read with
                    Sections =
                        read.Sections
                        |> filterSections (fun section ->
                            section.Level <> sectionLevel
                            || List.contains (titleText section) titles) } }

/// 基本情報の節の「入手方法」の小見出し以降(入手方法・ステータス・装備)を切り落とす。
/// role-playの参照に要るのはプロフィールと紹介文だけで、性能データはrole-playには使わないため。
/// 「入手方法」はプロフィールの表を横断する見出しセルの行で、節の見出しにはならない。
/// 次の節の見出しまでを落とす。
/// 基本情報をタブで切り替えるページでは、それぞれのタブの中身で同じように切り落とす。
let trimGameplayDetails (article: Article) : Article =
    let isAcquisition (block: Block) : bool =
        match block with
        | Caption content -> (plainText content).Trim() = "入手方法"
        | _ -> false

    let isSectionHeading (block: Block) : bool =
        match block with
        | Heading(level, _) -> level <= sectionLevel
        | _ -> false

    let rec trim (blocks: Block list) : Block list =
        blocks
        |> List.fold
            (fun (kept, skipping) block ->
                if isSectionHeading block then
                    block :: kept, false
                elif skipping || isAcquisition block then
                    kept, true
                else
                    match block with
                    | Tabs panels ->
                        Tabs(panels |> List.map (fun (label, inner) -> label, trim inner)) :: kept,
                        false
                    | _ -> block :: kept, false)
            ([], false)
        |> fst
        |> List.rev

    { article with
        Blocks = trim article.Blocks }

/// 記事をMarkdownへ書き出す。残った本文から参照されている脚注の定義だけを末尾に置く。
let render (article: Article) : string =
    MarkdownWriter.render (pruneFootnotes article)

/// 出典URLからナレッジファイル先頭に付ける出典の表記を組み立てる。
/// リンクには渡されたURLをそのまま使い、ページ名を経由した再エンコードの往復をしない。
/// 表示するページ名はクエリのパーセントデコードで復元し、
/// クエリの無いURLはページ名を復元できないのでURL全体を表示名にする。
/// Uri.ToStringはパーセントエンコードを解いた表示用文字列を返すため、リンクにはAbsoluteUriを使う。
let sourceHeader (source: Uri) : string =
    let pageName =
        match source.Query.TrimStart '?' with
        | Str.NullOrEmpty -> source.AbsoluteUri
        | Str.NonEmpty query -> Uri.UnescapeDataString query

    $"出典: [%s{pageName} - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{source.AbsoluteUri})\n\n"

/// sourceHeaderが組み立てる出典の行。リンクのURLを返す。
/// 生成したファイルから出典を読み戻す側が、書く側と同じ場所で形を知るためにここへ置く。
let sourceLine: Parse.TextParser<string> = pstring "出典: " >>. Markdown.inlineLink

/// ページ名からナレッジファイル先頭に付ける出典の表記を組み立てる。
let knowledgeHeader (pageName: string) : string = sourceHeader (pageUri pageName)

/// 出力先の親ディレクトリを作ってからファイルへ書き出す。
let private writeFile (outputPath: string) (content: string) : Task<unit> =
    task {
        match Path.GetDirectoryName outputPath with
        | Str.NullOrEmpty -> ()
        | Str.NonEmpty directory -> Directory.CreateDirectory directory |> ignore

        do! File.WriteAllTextAsync(outputPath, content)
    }

/// wikiruのページを取得し、ノイズを除いたDOMをactionへ渡してその結果を返す。
let private fetchPage (noise: string list) (pageName: string) (action: IDocument -> 'T) : Task<'T> =
    Page.fetch (pageUri pageName) (fun document ->
        WikiruDom.removeNoise noise document
        action document)

/// wikiruの記事ページを取得して、整えた記事の木として読む。
let fetchArticle (noise: string list) (pageName: string) : Task<Article> =
    task {
        let! article = fetchPage noise pageName readArticle
        return prepare article
    }

/// wikiruの記事ページをナレッジ用Markdownへ変換する。
let fetchMarkdown (pageName: string) : Task<string> =
    task {
        let! article = fetchArticle WikiruDom.articleNoise pageName
        return render article
    }

/// wikiruの記事をMarkdown化し、出典ヘッダ付きのナレッジファイルとして書き出す。
/// スキルが参照するリファレンスファイルの生成の入口。
/// 整形は掛けず、書き出したパスを返す。
/// 一括生成では複数の書き出しを終えてからnix fmtを1回で掛けるため、
/// 整形の呼び出しは書き出しと分けてTargetが持つ。
let writeKnowledge (pageName: string) (outputPath: string) : Task<string list> =
    task {
        let! markdown = fetchMarkdown pageName
        do! writeFile outputPath (knowledgeHeader pageName + markdown)
        return [ outputPath ]
    }

/// wikiruの記事を記事の木として読み、Markdownへ書き出す前の構造をJSONで書き出す。
/// ノイズの組や読み方を調整する時に、解釈した構造を確認するための入口。
let writeArticleJson (pageName: string) (outputPath: string) : Task<unit> =
    task {
        let! article = fetchArticle WikiruDom.articleNoise pageName
        do! writeFile outputPath (JsonCodec.serialize article)
    }

/// wikiruの生徒個別ページを取得し、指定した見出しの節だけの記事の木として読む。
let private fetchCharacterSections
    (noise: string list)
    (titles: string list)
    (pageName: string)
    : Task<Article> =
    task {
        let! article = fetchArticle noise pageName
        return selectSections titles article
    }

/// wikiruの生徒個別ページを読む。事実を載せているセクションだけへ選別する。
let fetchStudentArticle (pageName: string) : Task<Article> =
    fetchCharacterSections WikiruDom.characterNoise studentSectionTitles pageName

/// wikiruのプレイアブルではない登場人物のページを読む。
/// 生徒個別ページと同じ読み方で、事実を載せているセクションだけへ選別する。
let fetchUnplayableArticle (pageName: string) : Task<Article> =
    fetchCharacterSections WikiruDom.characterNoise unplayableSectionTitles pageName

/// wikiruの生徒個別ページをrole-playスキルの参照用に読む。
/// プロフィールと紹介文とボイスと小ネタだけを残す。
let fetchRolePlayArticle (pageName: string) : Task<Article> =
    task {
        let! article = fetchCharacterSections WikiruDom.rolePlayNoise rolePlaySectionTitles pageName
        return trimGameplayDetails article
    }

/// ナレッジ本体に節が1つも無かった時のページ名。
exception StudentSectionNotFound of pageName: string

/// プロフィールに「フルネーム」の項目が無かった時のページ名。
exception StudentFullNameNotFound of pageName: string

/// フルネームがページ名と名前で繋がらなかった時のページ名とフルネーム。
exception StudentFullNameMismatch of pageName: string * fullName: string

/// フルネームに手書きで添えられたふりがなの括弧。
let private ruby: Parse.TextParser<char> =
    pchar '(' >>. skipManySatisfies (fun c -> c <> ')') >>. pchar ')'

/// プロフィールの「フルネーム」の項目の値から、ふりがなを外したもの。
/// 項目が無いか、空欄で空白しか残らない時はNone。
/// ふりがなはruby要素で書かれていて、読みを捨てれば名前だけが残る。
/// 括弧で手書きしたページへの備えとして、文字に残った半角括弧も落とす。
let fullName (article: Article) : string option =
    /// 読みを捨てた文字。
    let rec baseText (inlines: Inline list) : string =
        inlines
        |> List.map (fun inlineNode ->
            match inlineNode with
            | Ruby(text, _) -> text
            | Strong content
            | Emphasis content
            | Strikethrough content
            | Link(_, content) -> baseText content
            | _ -> plainText [ inlineNode ])
        |> String.concat ""

    /// 項目の並び。プロフィールをタブで切り替えるページのために、タブの中も表示の順に辿る。
    /// 先頭のタブがページ名の姿なので、最初に見つかったフルネームを使えばよい。
    let rec items (blocks: Block list) : Part list list =
        blocks
        |> List.collect (fun block ->
            match block with
            | Items groups -> groups |> List.collect _.Items
            | Tabs panels -> panels |> List.collect (snd >> items)
            | _ -> [])

    items article.Blocks
    |> List.tryPick (fun parts ->
        match parts with
        | Key key :: rest when (plainText key.Inlines).Trim() = "フルネーム" ->
            rest
            |> List.tryPick (fun part ->
                match part with
                | Value(_, content) -> Some(baseText content.Inlines)
                | Key _ -> None)
            |> Option.defaultValue ""
            |> Some
        | _ -> None)
    |> Option.bind (fun value ->
        match (Parse.remove ruby value).Trim() with
        | Str.NullOrEmpty -> None
        | Str.NonEmpty name -> Some name)

/// フルネームのうちページ名の先頭と重なる部分より前を姓として、ページ名の前へ置く。
/// 重なる部分が無ければ、別人の名前を作らないように止まる。
let private prependFamilyName (pageName: string) (fullName: string) : string =
    let overlap =
        seq { 0 .. fullName.Length - 1 }
        |> Seq.tryFind (fun index ->
            pageName.StartsWith(fullName.Substring index, StringComparison.Ordinal))

    match overlap with
    | Some index -> fullName.Substring(0, index) + pageName
    | None -> raise (StudentFullNameMismatch(pageName, fullName))

/// ページ名の前へ姓を補う。
/// wikiruのページ名は「アイリ」「アイリ（バンド）」のように姓を持たないが、
/// スキルの説明は本文を読み込む前の一覧としてしか読まれないため、
/// 姓が無いとそこだけでは誰のことなのかが決まらない。
/// フルネームのうちページ名の先頭と重なる部分より前を姓としてページ名の前へ置く。
/// 衣装の接尾辞はページ名の側に残るので「アイリ（バンド）」は「栗村アイリ（バンド）」になり、
/// 姓を持たない「ケイ」ではフルネーム全体が重なって何も足さない。
let familyNamedPageName (pageName: string) (fullName: string option) : string =
    match fullName with
    | Some fullName -> prependFamilyName pageName fullName
    | None -> raise (StudentFullNameNotFound pageName)

/// プレイアブルではない登場人物のページ名の前へ、分かっていれば姓を補う。
/// 作中で姓が明かされていない人物や、アロナのように生徒ではない人物がいて、
/// フルネームの項目が無いか空欄のページがあるため、その時はページ名をそのまま使う。
/// フルネームがあるのにページ名と繋がらない時は、生徒と同じく別人の名前を作らないように止まる。
let unplayableDisplayName (pageName: string) (fullName: string option) : string =
    match fullName with
    | Some fullName -> prependFamilyName pageName fullName
    | None -> pageName

/// 記事に実際に含まれる節のh2の見出しを、現れる順に並べる。
/// 節を分けているのはh2なので、h3以下の小見出しは含めない。
/// ホワイトリストをそのまま書くと、
/// 愛用品の節を持たないコトリ（応援団）のようなページで、存在しない節を存在すると宣言してしまう。
/// これは何が載っているかを述べるための一覧なので、重複は現れる順を保ったまま潰す。
let sectionTitles (article: Article) : string list =
    let rec titles (section: Section) : string list =
        (if section.Level = sectionLevel then
             [ titleText section ]
         else
             [])
        @ List.collect titles section.Children

    (outline article.Blocks).Sections |> List.collect titles |> List.distinct

/// 生徒個別のスキルとプレイアブルではない登場人物のスキルのSKILL.md全体を組み立てる。
/// 1人分のナレッジは別ファイルへ分けるほどの量にならないため、
/// フロントマターと使い方の説明とナレッジ本体を1つのSKILL.mdに収める。
/// 事実を引くための参照専用で、ユーザがスラッシュコマンドとして呼ぶ意味が無いため、
/// user-invocable: falseでコマンドの一覧からは外す。
/// 文面だけを組み立てる。節の名前の並びと説明に載せる名前と本文は、呼び出し側が記事から作って渡す。
/// 本文の作り方が変わっても、文面のテストが巻き込まれないようにするためである。
let characterSkillMarkdown
    (englishNoun: string)
    (japaneseNoun: string)
    (skillName: string)
    (pageName: string)
    (displayName: string)
    (sections: string list)
    (body: string)
    : string =
    // 節を1つも読み取れないまま組み立てると、
    // 何が載っているかを述べない壊れた説明のスキルを書き出すことになる。
    let sections =
        match sections with
        | [] -> raise (StudentSectionNotFound pageName)
        | titles -> String.concat "・" titles

    // スキルの説明は本文を読み込まなくても一覧としてモデルのコンテキストへ載るため、
    // 300人分が積み上がる長さにはせず、誰の事実を引けるかだけを述べる。
    // 何が載っているかは下の「データの構造」が節の一覧として持っている。
    let description = $"Facts about Blue Archive %s{englishNoun} %s{displayName}."

    $"""---
name: %s{skillName}
description: %s{description}
user-invocable: false
---

『ブルーアーカイブ』の%s{japaneseNoun}「%s{pageName}」のゲーム内外の事実を調べるためのスキルです。

# データの構造

- セクションは%s{sections}です
- 表は「列名: 値」の箇条書きに、表の題名はその下の見出しになっています
- 別バージョン(衣装違い)の生徒は別のページなので、このデータには含まれません

# 使う時の注意

- データに無い情報は、別バージョンの生徒のスキルや出典のページで確認してください。似た名前の生徒の性能を混ぜないでください
- 小ネタはゲーム内外の事実のまとめです。一方、wiki執筆者による解説や運用考察は著作権方針により含めていません。必要な場合は出典のページを直接参照してください

# ナレッジ

%s{knowledgeHeader pageName}%s{body}"""

/// 生徒スキルのSKILL.md全体を組み立てる。説明には姓を補った名前を載せる。
/// 節の有無を名前より先に確かめるため、節が無ければ名前を作らずに止まる。
let studentSkillMarkdown (skillName: string) (pageName: string) (article: Article) : string =
    let sections = sectionTitles article

    let displayName =
        if List.isEmpty sections then
            pageName
        else
            familyNamedPageName pageName (fullName article)

    characterSkillMarkdown "student" "生徒" skillName pageName displayName sections (render article)

/// プレイアブルではない登場人物のスキルのSKILL.md全体を組み立てる。
/// ロールプレイの対象にせず、生徒の会話で話題に上った時に設定を引くためのナレッジだけを持つ。
/// 生徒個別ページと同じ形のページなので、本文の組み立ては生徒と共有する。
/// 未実装の生徒も生徒なので、wikiruのレアリティの表記のNPCとは呼ばない。
/// アロナのように生徒ではない人物も含むため、説明と本文ではcharacterと登場人物と呼ぶ。
let unplayableSkillMarkdown (skillName: string) (pageName: string) (article: Article) : string =
    let sections = sectionTitles article

    let displayName =
        if List.isEmpty sections then
            pageName
        else
            unplayableDisplayName pageName (fullName article)

    characterSkillMarkdown
        "character"
        "登場人物"
        skillName
        pageName
        displayName
        sections
        (render article)

/// 生徒個別ページと同じ形のページを読み、組み立てたSKILL.mdとして書き出す。
/// スキル名は出力先のディレクトリ名と一致する必要があるため、出力パスから導出する。
/// 整形は掛けず、書き出したパスを返す。
let private writeCharacterSkill
    (fetch: string -> Task<Article>)
    (build: string -> string -> Article -> string)
    (pageName: string)
    (outputPath: string)
    : Task<string list> =
    task {
        let skillName =
            match Path.GetDirectoryName outputPath with
            | Str.NullOrEmpty ->
                raise (
                    ArgumentException($"スキル名を導出するディレクトリがありません: %s{outputPath}", nameof outputPath)
                )
            | Str.NonEmpty directory -> Path.GetFileName directory

        let! article = fetch pageName
        do! writeFile outputPath (build skillName pageName article)
        return [ outputPath ]
    }

/// wikiruの生徒個別ページを読み、SKILL.mdとして書き出す。
let writeStudentSkill (pageName: string) (outputPath: string) : Task<string list> =
    writeCharacterSkill fetchStudentArticle studentSkillMarkdown pageName outputPath

/// wikiruのプレイアブルではない登場人物のページを読み、SKILL.mdとして書き出す。
let writeUnplayableSkill (pageName: string) (outputPath: string) : Task<string list> =
    writeCharacterSkill fetchUnplayableArticle unplayableSkillMarkdown pageName outputPath

/// wikiruの生徒個別ページを読み、
/// role-playスキルが参照する衣装別の参照ファイルとして書き出す。
/// 整形は掛けず、書き出したパスを返す。
let writeRolePlayReference (pageName: string) (outputPath: string) : Task<string list> =
    task {
        let! article = fetchRolePlayArticle pageName
        do! writeFile outputPath (knowledgeHeader pageName + render article)
        return [ outputPath ]
    }

/// wikiruのキャラ呼称表ページを構造化データへパースし、
/// LLM参照用のreference.mdと機械読み出し用のJSONを一度の取得から書き出す。
/// JSONをリポジトリへ併置することで、
/// 後段の生成処理がwikiruへ再アクセスせずに呼称を読み出せるようにする。
/// 整形は掛けず、書き出した2つのパスを返す。
let writeAppellation
    (pageName: string)
    (markdownPath: string)
    (jsonPath: string)
    : Task<string list> =
    task {
        let! entries = fetchPage WikiruDom.structuredNoise pageName Appellation.parse

        do!
            writeFile
                markdownPath
                (knowledgeHeader pageName + Appellation.toReferenceMarkdown entries)

        let document: Appellation.Document =
            { Source = (pageUri pageName).AbsoluteUri
              Entries = entries }

        do! writeFile jsonPath (Appellation.toJson document)
        return [ markdownPath; jsonPath ]
    }

/// wikiruの学校別キャラクター一覧ページを取得して、所属のレコードの列へパースする。
let fetchSchoolEntries (pageName: string) : Task<School.Entry list> =
    fetchPage WikiruDom.structuredNoise pageName School.parse

/// wikiruのキャラクター一覧ページを取得して、並んでいる生徒個別ページのページ名を返す。
let fetchCharacterListPages (pageName: string) : Task<string list> =
    fetchPage WikiruDom.structuredNoise pageName CharacterList.parse

/// wikiruの学校別キャラクター一覧ページを構造化データへパースし、
/// LLM参照用のreference.mdと機械読み出し用のJSONを書き出す。
/// 整形は掛けず、書き出したパスを返す。
let writeSchool (pageName: string) (markdownPath: string) (jsonPath: string) : Task<string list> =
    task {
        let! entries = fetchSchoolEntries pageName
        do! writeFile markdownPath (knowledgeHeader pageName + School.toReferenceMarkdown entries)

        let document: School.Document =
            { Source = (pageUri pageName).AbsoluteUri
              Entries = entries }

        do! writeFile jsonPath (School.toJson document)
        return [ markdownPath; jsonPath ]
    }
