/// Markdownを見出しの階層で捉えて、意味の切れ目で分割する。
/// Open WebUIのKnowledgeは登録したファイルをチャンクへ割ってベクトル検索するため、
/// 大きなファイルをそのまま渡すと表や節の途中で千切れて検索に掛からなくなる。
/// あらかじめ見出し単位のファイルへ割っておくことで、
/// 検索でヒットする単位と読ませたい単位を揃える。
module BluePrompt.Markdown

open System.Text
open System.Text.RegularExpressions

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

/// ATX見出しの行。setext見出し(下線形式)はこのリポジトリの生成物に現れないため扱わない。
let private headingPattern = Regex @"^(#{1,6})\s+(.*\S)\s*$"

/// コードブロックのフェンス行。
/// 閉じフェンスは開いたものと同じ文字で同じ以上の長さである必要があるため、
/// 文字と長さを取り出す。
let private fencePattern = Regex @"^\s{0,3}(`{3,}|~{3,})"

/// 行を見出しと本文へ分類した中間表現。
type private Line =
    /// 見出しの深さと、`#`と前後の空白を落とした文字列。
    /// フィールドへ名前を付けるとパターンマッチで参照されず未使用の宣言になるため、
    /// 意味はこのコメントで補う。
    | HeadingLine of int * string
    /// 見出しではない行をそのまま持つ。
    | TextLine of string

/// 本文をコードフェンスの内外を見分けながら行の並びへ分解する。
/// フェンスの中の`#`で始まる行は見出しではなくコードなので、本文として扱う。
let private toLines (markdown: string) : Line list =
    let mutable openFence: string option = None

    markdown.Replace("\r\n", "\n").Split '\n'
    |> Array.toList
    |> List.map (fun line ->
        match openFence, fencePattern.Match line with
        | None, fence when fence.Success ->
            openFence <- Some fence.Groups[1].Value
            TextLine line
        | Some opened, fence when
            fence.Success
            && fence.Groups[1].Value[0] = opened[0]
            && opened.Length <= fence.Groups[1].Value.Length
            ->
            openFence <- None
            TextLine line
        | None, _ ->
            match headingPattern.Match line with
            | heading when heading.Success ->
                HeadingLine(heading.Groups[1].Value.Length, heading.Groups[2].Value)
            | _ -> TextLine line
        | Some _, _ -> TextLine line)

/// 先頭に続く本文行を取り出す。
let private takeText (lines: Line list) : string list * Line list =
    let body =
        lines
        |> List.takeWhile (fun line ->
            match line with
            | TextLine _ -> true
            | HeadingLine _ -> false)
        |> List.map (fun line ->
            match line with
            | TextLine text -> text
            | HeadingLine _ -> "")

    body, List.skip body.Length lines

/// 親より深い見出しを子の節として、同じ深さの見出しを兄弟として集める。
let rec private takeSections (parentLevel: int) (lines: Line list) : Section list * Line list =
    match lines with
    | HeadingLine(level, text) :: rest when parentLevel < level ->
        let body, afterBody = takeText rest
        let children, afterChildren = takeSections level afterBody

        let section =
            { Level = level
              Heading = Some text
              Body = body
              Children = children }

        let siblings, remaining = takeSections parentLevel afterChildren
        section :: siblings, remaining
    | _ -> [], lines

/// Markdownを見出しの木へ読み解く。
let parseSections (markdown: string) : Section =
    let body, afterBody = takeText (toLines markdown)
    // 見出しはどの深さでもルートの子孫になるため、残りは常に空になる。
    let children, _ = takeSections 0 afterBody

    { Level = 0
      Heading = None
      Body = body
      Children = children }

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

/// 祖先の文脈を前置して断片の本文を組み立てる。
/// 見出しを持つ祖先については、本文は他の断片にも現れて重複するため見出しだけを持ち回る。
/// 見出しを持たないルートの本文は出典のような文書全体の前書きで、
/// どの断片を単体で読む時も文脈になるためそのまま前置する。
let private render (ancestors: Section list) (lines: string list) : string =
    let ancestorLines =
        ancestors
        |> List.collect (fun section ->
            match section.Heading with
            | None -> section.Body
            | Some _ -> headingLines section)

    (ancestorLines @ lines |> String.concat "\n" |> _.Trim()) + "\n"

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
        // ルートの本文は全ての断片へ前置されるので独立させると重複し、
        // 空白だけの本文は断片にしても検索の役に立たないため、どちらも落とす。
        let own =
            if
                section.Heading.IsNone
                || List.forall System.String.IsNullOrWhiteSpace section.Body
            then
                []
            else
                [ { Headings = headingsOf ancestors section
                    Text = render ancestors (headingLines section @ section.Body) } ]

        own @ List.collect (collect maxBytes (ancestors @ [ section ])) section.Children

/// Markdownを、各断片がmaxBytes以下に収まるように見出しの単位で分割する。
/// 全体が上限に収まる場合は分割せず1つの断片になる。
let splitBySize (maxBytes: int) (markdown: string) : Fragment list =
    collect maxBytes [] (parseSections markdown)
