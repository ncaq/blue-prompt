/// 生徒個別ページの型から記事の木への書き戻し。
/// Markdownの組み立てはMarkdownWriterに任せて、ここでは専用の型を小見出しと項目の並びへ戻すだけにする。
/// 読んだ時と同じ形へ戻すので、専用の型を通しても書き出される文面はほぼ変わらない。
/// 変わるのは基本情報の小見出しだけで、括弧の全角と半角の揺れがここに書いた表記へ揃い、
/// 中身の無い小見出しは書かれなくなる。
module BluePrompt.CharacterWriter

open BluePrompt.Article
open BluePrompt.Character

let private cell (inlines: Inline list) : CellContent = { Inlines = inlines; Blocks = [] }

let private rowParts (row: Row) : Part list =
    (row.Key |> Option.map (cell >> Key) |> Option.toList)
    @ (row.Fields |> List.map (fun (label, value) -> Value(label, cell value)))

let private entryParts (entry: Entry) : Part list =
    rowParts
        { Key = Some entry.Key
          Fields = if List.isEmpty entry.Value then [] else [ "", entry.Value ] }

/// 項目の並びを1つの表として書く。項目が無ければ何も書かない。
let private items (title: string option) (parts: Part list list) : Block list =
    match parts with
    | [] -> []
    | _ -> [ Items [ { Title = title; Items = parts } ] ]

let private entries (values: Entry list) : Block list = items None (List.map entryParts values)

let private rows (values: Row list) : Block list = items None (List.map rowParts values)

let private charts (values: Chart list) : Block list =
    match values with
    | [] -> []
    | _ ->
        [ values
          |> List.map (fun chart ->
              { Title = Some chart.Title
                Items = List.map rowParts chart.Rows })
          |> Items ]

/// 小見出しと中身。中身が無ければ小見出しも書かない。
let private captioned (title: string) (blocks: Block list) : Block list =
    match blocks with
    | [] -> []
    | _ -> Caption [ Text title ] :: blocks

/// 節の直下の見出しの深さ。
let private subsectionLevel = 3

let private basic (value: BasicInformation) : Block list =
    let portrait =
        value.Portrait
        |> Option.map (fun image -> [ Value("画像", cell image) ])
        |> Option.toList

    items None (portrait @ List.map entryParts value.Profile)
    @ captioned "基本情報" value.Introduction
    @ captioned "登場シナリオ" value.Appearances
    @ captioned "入手方法" value.Acquisition
    @ captioned "ステータス(初期値/MAX値)" (entries value.Status)
    @ captioned "装備（装着可能Lv）" (entries value.Equipment)
    @ value.Supplement

let rec private category (level: int) (value: SkillCategory) : Block list =
    Heading(level, value.Title) :: charts value.Skills
    @ List.collect (category (level + 1)) value.Children

let private weapon (value: Weapon) : Block list =
    Caption value.Name :: value.Icon
    @ captioned "詳細" value.Description
    @ captioned "限界突破" (entries value.LimitBreak @ rows value.Growth)

let private gear (value: Gear) : Block list =
    Caption value.Name :: value.Icon
    @ captioned "詳細" value.Description
    @ captioned "基本情報" (rows value.Effects)
    @ captioned "強化" (rows value.Upgrades)

let private potential (value: Potential) : Block list =
    rows value.Growth
    @ items (Some "必要素材") (List.map rowParts value.Materials)
    @ value.Notes

let private bondBonus (value: BondBonus) : Block list =
    let totals =
        match charts value.Totals with
        | [] -> []
        | blocks -> Heading(subsectionLevel, [ Text "ボーナス合計" ]) :: blocks

    rows value.Ranks @ totals @ value.Notes

let private bondStory (value: BondStory) : Block list = rows value.Episodes @ value.Lobby

let private voice (value: VoiceGroup list) : Block list =
    value
    |> List.collect (fun group ->
        match group.Title with
        | Some title -> Caption title :: entries group.Lines
        | None -> entries group.Lines)

/// 節の中身をブロックの並びへ戻す。
let contentBlocks (content: Content) : Block list =
    match content with
    | Basic value -> basic value
    | Skills value -> List.collect (category subsectionLevel) value
    | UniqueWeapon value -> weapon value
    | FavoriteItem value -> gear value
    | Potential value -> potential value
    | BondBonus value -> bondBonus value
    | BondStory value -> bondStory value
    | Voice value -> voice value
    | Trivia blocks
    | Other(blocks, _) -> blocks

/// 節を分けている見出しの深さ。
let private sectionLevel = 2

/// 生徒個別ページを記事の木へ戻す。
let toArticle (character: Character) : Article =
    { Blocks =
        character.Preamble
        @ (character.Sections
           |> List.collect (fun section ->
               Heading(sectionLevel, section.Title) :: contentBlocks section.Content))
      Footnotes = character.Footnotes }
