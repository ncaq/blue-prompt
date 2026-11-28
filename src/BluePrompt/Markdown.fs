/// Markdownの分割と組み立て。
/// 分割は見出しの階層で捉えて、意味の切れ目で切る。
/// Open WebUIのKnowledgeは登録したファイルをチャンクへ割ってベクトル検索するため、
/// 大きなファイルをそのまま渡すと表や節の途中で千切れて検索に掛からなくなる。
/// あらかじめ見出し単位のファイルへ割っておくことで、
/// 検索でヒットする単位と読ませたい単位を揃える。
module BluePrompt.Markdown

open System
open System.Text
open Markdig.Syntax
open Markdig.Syntax.Inlines
open XParsec
open XParsec.Parsers
open XParsec.CharParsers

/// GFMのパイプテーブルのセルとして安全なように縦棒をエスケープする。
/// wikiの本文由来の文字列をテーブルへ組み立てる箇所が複数あり、
/// エスケープの規則がその一箇所だけで直されて他が崩れるのを防ぐためここへ置く。
let escapeTableCell (text: string) : string = text.Replace("|", "\\|")

/// リンクを拾うためのMarkdigの解釈の設定。
/// wikiruから作る生成物はGFMの脚注を使う。
/// 脚注の拡張が無いと`[^1]: 本文`をリンク参照の定義として読み、
/// 本文中の`[^1]`が脚注の本文を参照先に持つリンクになってしまう。
/// `Markdig`を開くとこのモジュールと同じ名前の型が見えるようになるため、拡張メソッドは静的に呼ぶ。
let private linkPipeline =
    Markdig.MarkdownExtensions.UseFootnotes(Markdig.MarkdownPipelineBuilder()).Build()

/// 文書の中のリンクと画像の参照先を、登場順に列挙する。
/// 解釈はMarkdigに任せるので、コードの中の`[x](y)`をリンクとして拾わず、
/// 表示名の中の括弧やtitle付きの参照先も読み違えない。
/// 参照先が空のリンクは何も指さないので含めない。
let linkUrls (markdown: string) : string list =
    (Markdig.Markdown.Parse(markdown, linkPipeline)).Descendants<LinkInline>()
    |> Seq.choose (fun link ->
        match link.Url with
        | Str.NullOrEmpty -> None
        | Str.NonEmpty url -> Some url)
    |> List.ofSeq

/// インラインリンク(「[表示名](参照先)」)。参照先を返す。
/// 表示名は空でもよく、参照先は1文字以上を求める。
/// 出典の行のように決まった位置にあるリンクを、他のパーサと繋いで読むために使う。
/// 文書全体からリンクを拾う時は、Markdownとして解釈するlinkUrlsを使う。
let inlineLink: Parse.TextParser<string> =
    pchar '['
    >>. skipManySatisfies (fun c -> c <> ']')
    >>. pstring "]("
    >>. many1Chars (satisfy (fun c -> c <> ')'))
    .>> pchar ')'

/// 見出しで区切った節の木。
/// ルートは文書全体を表し、見出しを持たない。
type Section =
    {
        /// 見出しの`#`の数。ルートは0。
        Level: int
        /// 見出しの文字列。`#`と前後の空白は含まない。ルートはNone。
        Heading: string option
        /// この見出しの直下で、最初の子見出しより前にある行。
        Body: string list
        /// より深い見出しの節。
        Children: Section list
    }

/// 分割して得た断片1つ。
type Fragment =
    {
        /// ルートからこの断片までの見出しの並び。
        /// ファイル名や説明のように、本文の外で断片を指し示すために使う。
        Headings: string list
        /// 祖先の見出し行を前置した断片の本文。
        /// 単体で読んでも文脈が分かるようにするため、
        /// 元の文書での位置を見出しとして持ち回る。
        Text: string
    }

/// Markdownの見出しの最も深いレベル。
let private maxHeadingDepth: int = 6

/// ATX見出しの先頭の`#`の並び。数を見出しの深さとして返す。
/// maxHeadingDepthより長い並びは見出しではない。
/// setext見出し(下線形式)はこのリポジトリの生成物に現れないため扱わない。
let private headingMarker: Parse.TextParser<int> =
    many1Chars (pchar '#')
    >>= fun hashes ->
        if hashes.Length <= maxHeadingDepth then
            preturn hashes.Length
        else
            pzero

/// 行をATX見出しとして読み、深さと、`#`と前後の空白を落とした文字列を返す。
/// 文字列が空白しか残らない行は見出しとして扱わない。
let private tryHeading (line: string) : (int * string) option =
    Parse.tryPrefix (headingMarker .>> Parse.inlineSpaces1 .>>. Parse.restOfLine) line
    |> Option.bind (fun struct (level, rest) ->
        match rest.Trim() with
        | Str.NullOrEmpty -> None
        | Str.NonEmpty text -> Some(level, text))

/// コードブロックのフェンス行の先頭。フェンスの文字の並びを返す。
/// 字下げは3つまでで、それより深い行はフェンスではない。
/// 閉じフェンスは開いたものと同じ文字で同じ以上の長さである必要があるため、
/// 文字と長さが分かるように並びをそのまま返す。
let private fence: Parse.TextParser<string> =
    /// フェンスになる長さの、同じ文字の並び。
    let run (mark: char) : Parse.TextParser<string> =
        many1Chars (pchar mark)
        >>= fun marks -> if 3 <= marks.Length then preturn marks else pzero

    manyChars (satisfy Char.IsWhiteSpace)
    >>= fun indent -> if indent.Length <= 3 then run '`' <|> run '~' else pzero

/// コードフェンスで囲まれたブロックの行。
/// 開いたフェンスの行から、同じ文字で同じ以上の長さの閉じフェンスの行までを読む。
/// 閉じられていなければ文書の末尾までがブロックになる。
/// 中の`#`で始まる行は見出しではなくコードなので、見出しより先にこちらで読む。
let private fencedBlock: Parse.TextParser<string list> =
    let fenceOf (line: string) : string option = Parse.tryPrefix fence line

    Parse.lineChoose (fun line -> fenceOf line |> Option.map (fun opening -> line, opening))
    >>= fun (openingLine, opening) ->
        let closes (line: string) : bool =
            match fenceOf line with
            | Some closing -> closing[0] = opening[0] && opening.Length <= closing.Length
            | None -> false

        many (Parse.lineChoose (fun line -> if closes line then None else Some line))
        .>>. opt Parse.anyLine
        |>> fun struct (inner, closingLine) ->
            openingLine :: List.ofSeq inner @ ValueOption.toList closingLine

/// 見出しではない行の並び。
let private body: Parse.TextParser<string list> =
    let textLine: Parse.TextParser<string list> =
        Parse.lineChoose (fun line ->
            if Option.isSome (tryHeading line) then
                None
            else
                Some [ line ])

    many (fencedBlock <|> textLine) |>> (Seq.concat >> List.ofSeq)

/// 見出しに続く本文と、parentLevelより深い見出しの節の並び。
/// 同じ深さ以下の見出しは読まずに残すので、兄弟や親の兄弟として読まれる。
let rec private contents
    (parentLevel: int)
    : Parse.TextParser<struct (string list * Section list)> =
    fun reader -> (body .>>. (many (section parentLevel) |>> List.ofSeq)) reader

/// parentLevelより深い見出しの節1つ。
and private section (parentLevel: int) : Parse.TextParser<Section> =
    Parse.lineChoose (fun line ->
        tryHeading line |> Option.filter (fun (level, _) -> parentLevel < level))
    >>= fun (level, text) ->
        contents level
        |>> fun struct (body, children) ->
            { Level = level
              Heading = Some text
              Body = body
              Children = children }

/// Markdownを見出しの木へ読み解く。
let parseSections (markdown: string) : Section =
    match contents 0 (Reader.ofString (markdown.Replace("\r\n", "\n")) ()) with
    | Ok struct (body, children) ->
        { Level = 0
          Heading = None
          Body = body
          Children = children }
    // 本文も節も0個から読め、見出しはどの深さでもルートの子孫になるため、失敗しない。
    | Error _ -> invalidOp "Markdownを節へ読み解けませんでした"

/// 子孫の節のうちkeepが偽を返したものを、その子孫ごと落とす。
let rec filterDescendants (keep: Section -> bool) (section: Section) : Section =
    { section with
        Children = section.Children |> List.filter keep |> List.map (filterDescendants keep) }

/// 節の見出し行を組み立てる。ルートは見出しを持たないため空になる。
let private headingLines (section: Section) : string list =
    match section.Heading with
    | None -> []
    | Some text -> [ String.replicate section.Level "#" + " " + text ]

/// 節を子孫まで含めてMarkdownの行へ戻す。
let rec private renderSection (section: Section) : string list =
    headingLines section
    @ section.Body
    @ List.collect renderSection section.Children

/// 節の木をMarkdownへ戻す。見出し行は`#`と空白1つと文字列の形へ揃う。
/// 読む時に落とした末尾の改行と、`\n`へ揃えた改行コードは元へ戻らないので、
/// 文書の終わり方は呼び出し側で整える。
let toMarkdown (section: Section) : string =
    renderSection section |> String.concat "\n"

/// 祖先の見出し行を前置して断片の本文を組み立てる。
/// 祖先の本文は他の断片にも現れて重複するため、見出しだけを持ち回る。
///
/// 文書全体の前書きも前置したくなりますが、それはしません。
/// ベクトル検索では、前書きが独立した短いチャンクとして切り出された上に、
/// 全ての断片が同じ書き出しになって似たベクトルへ寄ってしまい、
/// どんな問い合わせにも中途半端に一致して、
/// 本当に読ませたい実データのチャンクを検索結果から押し出します。
let private render (ancestors: Section list) (lines: string list) : string =
    (List.collect headingLines ancestors @ lines |> String.concat "\n" |> _.Trim())
    + "\n"

/// UTF-8で符号化した時のバイト数。
/// Open WebUIへ渡るのはこの形なので、文字数ではなくバイト数で大きさを測る。
let private utf8Length (text: string) : int = Encoding.UTF8.GetByteCount text

/// 祖先とその節自身の見出しの並び。
let private headingsOf (ancestors: Section list) (section: Section) : string list =
    (ancestors @ [ section ]) |> List.choose _.Heading

/// 節を、maxBytes以下に収まる最も大きな見出しの単位まで降りて断片にする。
/// これ以上分けられない節は上限を超えていてもそのまま1つの断片にする。
let rec private collect
    (maxBytes: int)
    (ancestors: Section list)
    (section: Section)
    : Fragment list =
    let whole = render ancestors (renderSection section)

    if utf8Length whole <= maxBytes || List.isEmpty section.Children then
        [ { Headings = headingsOf ancestors section
            Text = whole } ]
    else
        // 子へ降りる時、この節自身の本文はどの子にも属さないため独立した断片にする。
        // 空白だけの本文は断片にしても検索の役に立たないので落とす。
        let own =
            if List.forall String.IsNullOrWhiteSpace section.Body then
                []
            else
                [ { Headings = headingsOf ancestors section
                    Text = render ancestors (headingLines section @ section.Body) } ]

        own @ List.collect (collect maxBytes (ancestors @ [ section ])) section.Children

/// Markdownを、各断片がmaxBytes以下に収まるように見出しの単位で分割する。
/// 全体が上限に収まる場合は分割せず1つの断片になる。
let splitBySize (maxBytes: int) (markdown: string) : Fragment list =
    collect maxBytes [] (parseSections markdown)
