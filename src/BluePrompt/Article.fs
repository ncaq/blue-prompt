/// 記事を解釈した汎用の木と、その上の小さな道具。
/// DOMを読むのは解釈までにして、節の選別や表の読み解きはこの不変の値の上の純粋関数で行い、
/// Markdownは最後に1回だけ組み立てる。
/// Markdownの文字列へ早く落としてから文字列を読み直す作りだと、
/// 脚注や節の切れ目のように構造として分かっていたものを、表記から推し量り直すことになるため。
module BluePrompt.Article

open System

/// 行の中に並ぶ要素。
type Inline =
    | Text of string
    | Strong of Inline list
    | Emphasis of Inline list
    | Strikethrough of Inline list
    /// ふりがな付きの文字。フルネームから読みを外す時に、括弧のパースではなく型で読み分けられる。
    | Ruby of text: string * reading: string
    | Link of target: Uri * content: Inline list
    | Image of alt: string * source: Uri
    | FootnoteReference of label: string
    | LineBreak

/// 縦に並ぶ要素。
/// 見出しは節の入れ子ではなく並びの中の1要素として持ち、節の木はoutlineで導く。
/// HTMLもMarkdownも見出しは兄弟として並ぶので、読む時も書く時もこの形が素直なため。
type Block =
    | Heading of level: int * content: Inline list
    | Paragraph of Inline list
    | BulletList of items: Block list list
    | OrderedList of items: Block list list
    | Quote of Block list
    | CodeBlock of string
    | ThematicBreak
    /// ラベルを選んで中身を切り替える並び。ラベルと、その中身の組を表示の順に持つ。
    /// 中身の見出しは節を分けない。切り替えの全体が、置かれた節の中身である。
    | Tabs of (Inline list * Block list) list
    /// 解釈した直後の表。結合セルを保つ。Listing.analyzeがCaptionとItemsへ読み解く。
    | Table of Table
    /// 表を横断する見出しセルだけの行。表の中の小見出し。
    | Caption of Inline list
    /// 表を読み解いた、ラベル付きの項目の並び。
    | Items of ItemGroup list

and Table = { Rows: Cell list list }

and Cell =
    {
        /// 見出しセル(th)か。
        IsHeader: bool
        /// 縦に結合する行数。HTMLの仕様どおり、0は「末尾まで」を表す。
        RowSpan: int
        ColumnSpan: int
        Content: Block list
    }

/// 題名を共有する項目の並び。スキルの表の「Q.E.D」のように、表が何の一覧かを言う行が題名になる。
and ItemGroup =
    { Title: string option
      Items: Part list list }

/// 項目を組み立てる部品。行の見出しのセルはキーで、それ以外は列名を添えた値。
and Part =
    | Key of CellContent
    | Value of label: string * CellContent

/// セルの中身。1行へ繋いだ文字と、1行へは繋げない入れ子のブロック。
and CellContent =
    { Inlines: Inline list
      Blocks: Block list }

type Footnote = { Label: string; Content: Inline list }

type Article =
    { Blocks: Block list
      Footnotes: Footnote list }

/// 見出しで区切った節の木。Blockの並びから導く見方で、書き換えたらBlockの並びへ戻す。
type Section =
    {
        Level: int
        Title: Inline list
        /// この見出しの直下で、最初の子見出しより前にあるブロック。
        Body: Block list
        Children: Section list
    }

type Outline =
    {
        /// 最初の見出しより前にあるブロック。
        Preamble: Block list
        Sections: Section list
    }

/// 並びの中の文字を繋ぐ。画像と脚注の参照は文字を持たない。
/// ふりがなは、表示される並びのとおり括弧に入れて後ろへ添える。
let rec plainText (inlines: Inline list) : string =
    inlines
    |> List.map (fun inlineNode ->
        match inlineNode with
        | Text text -> text
        | Strong content
        | Emphasis content
        | Strikethrough content
        | Link(_, content) -> plainText content
        | Ruby(text, reading) -> $"%s{text}(%s{reading})"
        | Image _
        | FootnoteReference _ -> ""
        | LineBreak -> "\n")
    |> String.concat ""

/// 並びのどこかに画像があるか。
let rec hasImage (inlines: Inline list) : bool =
    inlines
    |> List.exists (fun inlineNode ->
        match inlineNode with
        | Image _ -> true
        | Strong content
        | Emphasis content
        | Strikethrough content
        | Link(_, content) -> hasImage content
        | Text _
        | Ruby _
        | FootnoteReference _
        | LineBreak -> false)

/// 文字も画像も持たない並びか。
/// 画像は文字を持たないが、立ち絵や素材のアイコンのように画像だけで値になるため中身として数える。
let isBlank (inlines: Inline list) : bool =
    String.IsNullOrWhiteSpace(plainText inlines) && not (hasImage inlines)

/// セルの中身が空か。
let isBlankContent (content: CellContent) : bool =
    isBlank content.Inlines && List.isEmpty content.Blocks

/// 中身を持たないブロックか。
let rec isBlankBlock (block: Block) : bool =
    match block with
    | Heading(_, content)
    | Paragraph content
    | Caption content -> isBlank content
    | BulletList items
    | OrderedList items -> items |> List.forall (List.forall isBlankBlock)
    | Quote blocks -> List.forall isBlankBlock blocks
    | Tabs panels -> panels |> List.forall (snd >> List.forall isBlankBlock)
    | CodeBlock text -> String.IsNullOrWhiteSpace text
    | ThematicBreak -> false
    | Table table ->
        table.Rows
        |> List.forall (List.forall (fun cell -> List.forall isBlankBlock cell.Content))
    | Items groups -> groups |> List.forall (fun group -> List.isEmpty group.Items)

/// 全ての並びをfで書き換える。入れ子のブロックと表のセルと項目の中身まで降りる。
/// 並びの中の入れ子(強調の中など)へ降りるかどうかはfが決める。
let rec mapInlines (f: Inline list -> Inline list) (blocks: Block list) : Block list =
    let content (cellContent: CellContent) : CellContent =
        { Inlines = f cellContent.Inlines
          Blocks = mapInlines f cellContent.Blocks }

    blocks
    |> List.map (fun block ->
        match block with
        | Heading(level, inlines) -> Heading(level, f inlines)
        | Paragraph inlines -> Paragraph(f inlines)
        | Caption inlines -> Caption(f inlines)
        | BulletList items -> BulletList(List.map (mapInlines f) items)
        | OrderedList items -> OrderedList(List.map (mapInlines f) items)
        | Quote inner -> Quote(mapInlines f inner)
        | Tabs panels ->
            panels |> List.map (fun (label, inner) -> f label, mapInlines f inner) |> Tabs
        | Table table ->
            Table
                { Rows =
                    table.Rows
                    |> List.map (
                        List.map (fun cell ->
                            { cell with
                                Content = mapInlines f cell.Content })
                    ) }
        | Items groups ->
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
        | CodeBlock _
        | ThematicBreak -> block)

/// 全てのブロックをfで書き換える。fは1つのブロックを0個以上のブロックへ写す。
/// 入れ子の内側を先に書き換える。
let rec collectBlocks (f: Block -> Block list) (blocks: Block list) : Block list =
    blocks
    |> List.collect (fun block ->
        match block with
        | BulletList items -> f (BulletList(List.map (collectBlocks f) items))
        | OrderedList items -> f (OrderedList(List.map (collectBlocks f) items))
        | Quote inner -> f (Quote(collectBlocks f inner))
        | Tabs panels ->
            panels
            |> List.map (fun (label, inner) -> label, collectBlocks f inner)
            |> Tabs
            |> f
        | Heading _
        | Paragraph _
        | Caption _
        | CodeBlock _
        | ThematicBreak
        | Table _
        | Items _ -> f block)

/// 並びの中に現れる脚注の参照のラベル。入れ子まで降りる。
let rec private inlineFootnoteLabels (inlines: Inline list) : string list =
    inlines
    |> List.collect (fun inlineNode ->
        match inlineNode with
        | FootnoteReference label -> [ label ]
        | Strong content
        | Emphasis content
        | Strikethrough content
        | Link(_, content) -> inlineFootnoteLabels content
        | Text _
        | Ruby _
        | Image _
        | LineBreak -> [])

/// ブロックの並びから参照されている脚注のラベル。
let footnoteLabels (blocks: Block list) : Set<string> =
    let labels = Collections.Generic.HashSet<string>()

    mapInlines
        (fun inlines ->
            for label in inlineFootnoteLabels inlines do
                labels.Add label |> ignore

            inlines)
        blocks
    |> ignore

    Set.ofSeq labels

/// 本文から参照されている脚注の定義だけを残す。
/// 節の選別で参照ごと落ちた定義を残すと、宙に浮いた脚注になるため。
let pruneFootnotes (article: Article) : Article =
    let referenced = footnoteLabels article.Blocks

    { article with
        Footnotes =
            article.Footnotes
            |> List.filter (fun footnote -> Set.contains footnote.Label referenced) }

/// 並びの中の脚注の参照のラベルをrenameで付け替える。入れ子まで降りる。
let rec private renameInlineFootnotes
    (rename: string -> string)
    (inlines: Inline list)
    : Inline list =
    inlines
    |> List.map (fun inlineNode ->
        match inlineNode with
        | FootnoteReference label -> FootnoteReference(rename label)
        | Strong content -> Strong(renameInlineFootnotes rename content)
        | Emphasis content -> Emphasis(renameInlineFootnotes rename content)
        | Strikethrough content -> Strikethrough(renameInlineFootnotes rename content)
        | Link(target, content) -> Link(target, renameInlineFootnotes rename content)
        | Text _
        | Ruby _
        | Image _
        | LineBreak -> inlineNode)

/// 記事の脚注のラベルを、参照と定義を揃えてrenameで付け替える。
/// 別々に読んだ記事を1つへ繋ぐ時に、ラベルが重ならないようにするために使う。
let renameFootnotes (rename: string -> string) (article: Article) : Article =
    { Blocks = mapInlines (renameInlineFootnotes rename) article.Blocks
      Footnotes =
        article.Footnotes
        |> List.map (fun footnote ->
            { Label = rename footnote.Label
              Content = renameInlineFootnotes rename footnote.Content }) }

/// 記事の並びを1つの記事へ繋ぐ。
/// 脚注のラベルは記事ごとに数え始めるので、そのまま繋ぐと同じラベルの定義が重なり、
/// GFMでは後の定義が捨てられて、後の記事の参照が前の記事の脚注を指してしまう。
/// 定義の並びの順に1から数え直したラベルへ付け替えて、繋いだ後も一意にする。
let concatArticles (articles: Article list) : Article =
    let renamed, _ =
        articles
        |> List.mapFold
            (fun offset article ->
                let numbers =
                    article.Footnotes
                    |> List.mapi (fun index footnote ->
                        footnote.Label, string<int> (offset + index + 1))
                    |> Map.ofList

                // 定義の無い参照は、付け替えずにそのまま残す。
                let rename label =
                    Map.tryFind label numbers |> Option.defaultValue label

                renameFootnotes rename article, offset + article.Footnotes.Length)
            0

    { Blocks = renamed |> List.collect _.Blocks
      Footnotes = renamed |> List.collect _.Footnotes }

/// parentLevelより深い見出しの節を先頭から読み、読めなかった残りと組で返す。
/// 同じ深さ以下の見出しは読まずに残すので、兄弟や親の兄弟として読まれる。
let rec private sections (parentLevel: int) (blocks: Block list) : Section list * Block list =
    match blocks with
    | Heading(level, title) :: rest when parentLevel < level ->
        let isHeading (block: Block) : bool =
            match block with
            | Heading _ -> true
            | _ -> false

        let body = List.takeWhile (isHeading >> not) rest
        let children, afterChildren = sections level (List.skip body.Length rest)
        let siblings, remaining = sections parentLevel afterChildren

        { Level = level
          Title = title
          Body = body
          Children = children }
        :: siblings,
        remaining
    | _ -> [], blocks

/// ブロックの並びを節の木へ読む。
/// 箇条書きや引用やタブの中の見出しは節を分けない。
let outline (blocks: Block list) : Outline =
    let isHeading (block: Block) : bool =
        match block with
        | Heading _ -> true
        | _ -> false

    let preamble = List.takeWhile (isHeading >> not) blocks
    // 見出しはどの深さでも深さ0より深いので、残りは全て節として読める。
    let read, _ = sections 0 (List.skip preamble.Length blocks)

    { Preamble = preamble; Sections = read }

/// 節を子孫まで含めてブロックの並びへ戻す。
let rec sectionBlocks (section: Section) : Block list =
    Heading(section.Level, section.Title) :: section.Body
    @ List.collect sectionBlocks section.Children

/// 節の木をブロックの並びへ戻す。
let ofOutline (outline: Outline) : Block list =
    outline.Preamble @ List.collect sectionBlocks outline.Sections

/// 子孫の節のうちkeepが偽を返したものを、その子孫ごと落とす。
let rec filterSections (keep: Section -> bool) (sections: Section list) : Section list =
    sections
    |> List.filter keep
    |> List.map (fun section ->
        { section with
            Children = filterSections keep section.Children })

/// 中身を持たない節を落とす。
/// 節の中身がノイズの除去や節の選別で全て落ちると、見出しだけが残る。
/// 小見出しが全て落ちて空になった上位の節も落ちるように、深い方から先に見る。
let rec dropEmptySections (sections: Section list) : Section list =
    sections
    |> List.map (fun section ->
        { section with
            Children = dropEmptySections section.Children })
    |> List.filter (fun section ->
        not (List.forall isBlankBlock section.Body)
        || not (List.isEmpty section.Children))
