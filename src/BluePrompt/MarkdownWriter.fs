/// 記事の木をMarkdownの文字列へ書き出す。
/// Markdownを組み立てるのはここ1箇所で、パイプラインの最後に1回だけ行う。
/// 書き出した後の整形はnix fmtのprettierが担うので、ここでは構造が正しく読まれる形だけを保証する。
module BluePrompt.MarkdownWriter

open System
open System.Text
open XParsec
open XParsec.Parsers
open XParsec.CharParsers
open BluePrompt.Article

/// 文字をMarkdownの地の文として書く。
/// エスケープは、そのままだとCommonMarkとGFMで構造として読まれる文字だけに絞る。
/// 読むのは主にLLMなので、要らないバックスラッシュは雑音になる。
///
/// - 下線は語の途中(前後がASCIIの英数字)なら強調にならないので、ファイル名のような文字列はそのまま書く
/// - チルダは2つ続いた時だけ取り消し線になるので、「1~1.5倍」のような単独のものはそのまま書く
let private escapeText (text: string) : string =
    let builder = StringBuilder()

    let isWord (index: int) : bool =
        0 <= index && index < text.Length && Char.IsAsciiLetterOrDigit text[index]

    let isTilde (index: int) : bool =
        0 <= index && index < text.Length && text[index] = '~'

    text
    |> String.iteri (fun index c ->
        match c with
        | '\\'
        | '`'
        | '*'
        | '['
        | ']'
        | '<'
        | '>' -> builder.Append('\\').Append c |> ignore
        | '_' when not (isWord (index - 1) && isWord (index + 1)) ->
            builder.Append("\\_") |> ignore
        | '~' when isTilde (index - 1) || isTilde (index + 1) -> builder.Append("\\~") |> ignore
        | _ -> builder.Append c |> ignore)

    builder.ToString()

/// リンクや画像の参照先を書く。
/// 括弧や空白を含むURLは、参照先の終わりを読み違えないように山括弧で囲む。
let private destination (target: Uri) : string =
    let url = target.AbsoluteUri

    if url.Contains '(' || url.Contains ')' || url.Contains ' ' then
        $"<%s{url}>"
    else
        url

/// 画像の代替文字を書く。角括弧だけが代替文字の終わりを読み違えさせる。
let private escapeAlt (alt: string) : string =
    alt.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")

/// 行の中の要素の並びを書く。改行はそのまま改行文字として残す。
let rec private inlineText (inlines: Inline list) : string =
    inlines
    |> List.map (fun inlineNode ->
        match inlineNode with
        | Text text -> escapeText text
        | Strong content -> $"**%s{inlineText content}**"
        | Emphasis content -> $"_%s{inlineText content}_"
        | Strikethrough content -> $"~~%s{inlineText content}~~"
        | Ruby(text, reading) -> $"%s{escapeText text}(%s{escapeText reading})"
        // 表示文字が参照先そのもののリンクは、同じURLを2回書かずに済む形で書く。
        | Link(target, content) when plainText content = target.AbsoluteUri ->
            $"<%s{target.AbsoluteUri}>"
        | Link(target, content) -> $"[%s{inlineText content}](%s{destination target})"
        | Image(alt, source) -> $"![%s{escapeAlt alt}](%s{destination source})"
        | FootnoteReference label -> $"[^%s{label}]"
        | LineBreak -> "\n")
    |> String.concat ""

/// 行の中の要素の並びを1行で書く。見出しや項目のように改行を持てない所に使う。
let private singleLine (inlines: Inline list) : string =
    (inlineText inlines).Replace("\n", " ").Trim()

/// 行頭でだけ構造として読まれる形。バックスラッシュを挟む位置を返す。
/// 段落の中の改行を行の区切りとして書くと、全ての行の先頭が行頭になるため、行ごとに確かめる。
let private lineStartMarkers: Parse.TextParser<int> list =
    /// 記号の後ろが空白か行末であること。入力は読み進めない。
    let markerEnd: Parse.TextParser<unit> = lookAhead ((pchar ' ' >>% ()) <|> eof)

    [
      // 見出し。
      many1Chars (pchar '#')
      >>= fun hashes -> if hashes.Length <= 6 then preturn 0 else pzero
      .>> markerEnd
      // 箇条書き。
      anyOf "-+" >>. markerEnd >>% 0
      // 番号付きの箇条書き。区切りの記号の前へ挟む。
      Parse.digits .>> anyOf ".)" .>> markerEnd |>> _.Length
      // 直前の行を見出しに変える下線と、水平線。
      many1Chars (pchar '=') >>. Parse.inlineSpaces >>. eof >>% 0
      many1Chars (pchar '-') >>. Parse.inlineSpaces >>. eof >>% 0
      // 脚注の定義。コロンの前へ挟む。
      pstring "[^" >>. many1Chars (satisfy (fun c -> c <> ']')) .>> pstring "]:"
      |>> fun label -> label.Length + 3 ]

/// 行頭でだけ構造として読まれる形をエスケープする。
let private escapeLineStart (line: string) : string =
    match lineStartMarkers |> List.tryPick (fun marker -> Parse.tryPrefix marker line) with
    | Some index -> line.Insert(index, "\\")
    | None -> line

/// 行の並びの先頭の行へ記号を、残りの行へ同じ幅の字下げを付ける。空行には何も付けない。
let private prefixLines (marker: string) (lines: string list) : string list =
    let indent = String.replicate marker.Length " "

    lines
    |> List.mapi (fun index line ->
        if index = 0 then marker + line
        elif String.IsNullOrEmpty line then line
        else indent + line)

/// 見出しの深さの上限。
let private maxHeadingLevel = 6

/// 太字だけの段落。小見出しと、見出しにできない題名に使う。
/// 中身に強調があっても太字は重ねられないので、外側の太字へ畳む。
let private boldLine (inlines: Inline list) : string =
    let rec unwrap (inlines: Inline list) : Inline list =
        inlines
        |> List.collect (fun inlineNode ->
            match inlineNode with
            | Strong content -> unwrap content
            | _ -> [ inlineNode ])

    $"**%s{singleLine (unwrap inlines)}**"

/// 項目の部品を1行へ並べる。キーの後は「: 」で、値の後は「, 」で繋ぐ。
/// 値は列名があれば「列名: 値」の形にする。
let private itemLine (parts: Part list) : string =
    let rendered =
        parts
        |> List.map (fun part ->
            match part with
            | Key content -> singleLine content.Inlines, ": "
            | Value(Str.NullOrEmpty, content) -> singleLine content.Inlines, ", "
            | Value(Str.NonEmpty label, content) ->
                $"%s{escapeText label}: %s{singleLine content.Inlines}", ", ")

    rendered
    |> List.mapi (fun index (text, separator) ->
        if index = rendered.Length - 1 then
            text
        else
            text + separator)
    |> String.concat ""

/// ブロックの並びを行の並びとして書く。
/// headingLevelは直前の見出しの深さで、項目の題名をその1つ下の見出しにするために持ち回る。
/// 箇条書きや引用の中ではNoneを渡して、題名を見出しではなく太字の段落にする。
let rec private blocksLines (headingLevel: int option) (blocks: Block list) : string list =
    let _, chunks =
        blocks
        |> List.fold
            (fun (level: int option, chunks: (Block * string list) list) block ->
                let level =
                    match level, block with
                    | Some _, Heading(headingLevel, _) -> Some headingLevel
                    | _ -> level

                match blockLines level block with
                | [] -> level, chunks
                | lines -> level, (block, lines) :: chunks)
            (headingLevel, [])

    /// 前のブロックとの間に空行を置くか。
    /// 段落の直後の箇条書きは、空行を置かずに続けると入れ子の項目として詰めて読める。
    let separated (previous: Block) (next: Block) : bool =
        match headingLevel, previous, next with
        | None, Paragraph _, (BulletList _ | OrderedList _) -> false
        | _ -> true

    List.rev chunks
    |> List.fold
        (fun (lines: string list, previous: Block option) (block, blockLines) ->
            match previous with
            | Some previous when separated previous block -> lines @ "" :: blockLines, Some block
            | _ -> lines @ blockLines, Some block)
        ([], None)
    |> fst

/// ブロック1つを行の並びとして書く。
and private blockLines (headingLevel: int option) (block: Block) : string list =
    match block with
    | Heading(level, content) ->
        [ String.replicate (min level maxHeadingLevel) "#" + " " + singleLine content ]
    | Paragraph content ->
        (inlineText content).Split '\n'
        |> Array.map (fun line -> escapeLineStart (line.Trim ' '))
        |> Array.filter (String.IsNullOrEmpty >> not)
        |> Array.toList
    | BulletList items -> items |> List.collect (blocksLines None >> prefixLines "- ")
    | OrderedList items -> items |> List.collect (blocksLines None >> prefixLines "1. ")
    | Quote inner ->
        blocksLines None inner
        |> List.map (fun line -> if String.IsNullOrEmpty line then ">" else "> " + line)
    | CodeBlock text ->
        // 中身に現れるバッククォートの連なりより長いフェンスで囲む。
        let longestRun =
            Parse.findAll (many1Chars (pchar '`')) text
            |> List.map _.Length
            |> List.fold max 0

        let fence = String.replicate (max 3 (longestRun + 1)) "`"
        fence :: List.ofArray (text.Replace("\r\n", "\n").Split '\n') @ [ fence ]
    | ThematicBreak -> [ "---" ]
    | Caption content -> [ boldLine content ]
    | Items groups ->
        groups
        |> List.collect (fun group ->
            let title =
                match group.Title, headingLevel with
                | None, _ -> []
                | Some title, Some level when level < maxHeadingLevel ->
                    [ String.replicate (level + 1) "#" + " " + escapeText title ]
                // 見出しの深さを使い切った時と、箇条書きの中では、太字の段落で題名らしさを残す。
                | Some title, _ -> [ boldLine [ Text title ] ]

            let items =
                group.Items
                |> List.collect (fun parts ->
                    let nested =
                        parts
                        |> List.collect (fun part ->
                            match part with
                            | Key content
                            | Value(_, content) -> content.Blocks)

                    prefixLines "- " (itemLine parts :: blocksLines None nested))

            match title, items with
            | [], _ -> [ items ]
            | _, [] -> [ title ]
            | _ -> [ title; items ])
        |> List.filter (List.isEmpty >> not)
        |> fun chunks ->
            match chunks with
            | [] -> []
            | _ -> List.reduce (fun upper lower -> upper @ "" :: lower) chunks
    // 読み解く前の表が残っていても書けるように、ここで読み解いてから書く。
    | Table _ -> blocksLines headingLevel (Listing.analyze [ block ])

/// ブロックの並びをMarkdownの文字列にする。末尾の改行は付けない。
/// 項目の題名は直前の見出しの1つ下の見出しになり、見出しが無ければh2になる。
let renderBlocks (blocks: Block list) : string =
    blocksLines (Some 1) blocks |> String.concat "\n"

/// 記事をMarkdownの文字列にする。脚注の定義は本文の後ろへ1行ずつ置く。
let render (article: Article) : string =
    let footnotes =
        article.Footnotes
        |> List.map (fun footnote -> $"[^%s{footnote.Label}]: %s{singleLine footnote.Content}")

    // 中身の無い記事は、改行だけの文字列ではなく空の文字列にする。
    match
        [ renderBlocks article.Blocks; String.concat "\n" footnotes ]
        |> List.filter (String.IsNullOrEmpty >> not)
    with
    | [] -> ""
    | parts -> String.concat "\n\n" parts + "\n"
