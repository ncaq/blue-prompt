/// 記事の木から生徒個別ページの型への読み取り。
/// 表を読み解いた後の小見出し(Caption)と項目(Items)を読むので、
/// 結合セルや見出し行の判定を節ごとに書き直すことは無い。
/// 節の中身を全て読み切れた時だけ専用の型にして、
/// 読めないものが残った節は理由を添えて汎用の木のまま持つ。
module BluePrompt.CharacterReader

open BluePrompt.Article
open BluePrompt.Character

/// 節が専用の型の形をしていなかった時の理由。節ごとの読み取りの中だけで使い、外へは出さない。
exception private Unreadable of reason: string

let private fail (reason: string) : 'T = raise (Unreadable reason)

/// 項目の中身の並び。入れ子のブロックを持つ項目は、専用の型では表せない。
let private inlinesOf (content: CellContent) : Inline list =
    if List.isEmpty content.Blocks then
        content.Inlines
    else
        fail "項目の中に入れ子のブロックがあります"

let private row (parts: Part list) : Row =
    let key, fields =
        match parts with
        | Key key :: rest -> Some(inlinesOf key), rest
        | _ -> None, parts

    { Key = key
      Fields =
        fields
        |> List.map (fun part ->
            match part with
            | Value(label, content) -> label, inlinesOf content
            | Key _ -> fail "項目の途中にキーがあります") }

let private entry (parts: Part list) : Entry =
    match row parts with
    | { Key = Some key; Fields = [] } -> { Key = key; Value = [] }
    | { Key = Some key
        Fields = [ Str.NullOrEmpty, value ] } -> { Key = key; Value = value }
    | _ -> fail "キーと値の組ではない項目があります"

/// ブロックの並びを項目のまとまりの並びとして読む。項目ではないブロックが混ざっていれば読めない。
let private groups (blocks: Block list) : ItemGroup list =
    blocks
    |> List.collect (fun block ->
        match block with
        | Items groups -> groups
        | _ -> fail "項目の並びではないブロックがあります")

/// 題名を持たない1つのまとまりの項目。
let private untitled (blocks: Block list) : Part list list =
    match groups blocks with
    | [ { Title = None; Items = items } ] -> items
    | _ -> fail "題名の無い1つの表ではありません"

let private chart (group: ItemGroup) : Chart =
    match group.Title with
    | Some title ->
        { Title = title
          Rows = List.map row group.Items }
    | None -> fail "題名の無い表があります"

/// 小見出しの文字。括弧の全角と半角の揺れを半角へ寄せる。
let private label (inlines: Inline list) : string =
    (plainText inlines).Trim().Replace('（', '(').Replace('）', ')')

/// ブロックの並びを、最初の小見出しより前と、小見出しごとの中身へ分ける。
let private chunks (blocks: Block list) : Block list * (Inline list * Block list) list =
    let head, parts =
        blocks
        |> List.fold
            (fun (head, parts) block ->
                match block, parts with
                | Caption title, _ -> head, (title, []) :: parts
                | _, [] -> block :: head, parts
                | _, (title, body) :: rest -> head, (title, block :: body) :: rest)
            ([], [])

    List.rev head, parts |> List.rev |> List.map (fun (title, body) -> title, List.rev body)

let private basic (body: Block list) : BasicInformation =
    let head, parts = chunks body

    let portrait, profile =
        match untitled head with
        | [ Value("画像", image) ] :: rest -> Some(inlinesOf image), List.map entry rest
        | items -> None, List.map entry items

    /// 小見出しの並ぶ順。書き出す時にこの順で並べるので、違う順のページは読まない。
    let known = [ "基本情報"; "登場シナリオ"; "入手方法"; "ステータス(初期値/MAX値)"; "装備(装着可能Lv)" ]

    // 中身の無い小見出しは、無いものとして読む。
    // プレイアブルではない登場人物のページには、まだ何も書かれていない「登場シナリオ」だけが残っていることがある。
    let positions =
        parts
        |> List.map (fun (title, _) ->
            match List.tryFindIndex ((=) (label title)) known with
            | Some index -> index
            | None -> fail $"知らない小見出しがあります: %s{label title}")

    if positions <> List.sort (List.distinct positions) then
        fail "小見出しの順が違うか、同じ小見出しが2つあります"

    let find (name: string) : Block list =
        parts
        |> List.tryFind (fun (title, _) -> label title = name)
        |> Option.map snd
        |> Option.defaultValue []

    let isLast (name: string) : bool =
        match List.tryLast parts with
        | Some(title, _) -> label title = name
        | None -> false

    /// 表の小見出しの中身を、項目と、後ろに続く補足へ分ける。
    /// 補足を持てるのは最後の小見出しだけで、途中の小見出しに表以外があれば読まない。
    let table (name: string) : Entry list * Block list =
        match find name with
        | [] -> [], []
        | first :: rest when List.isEmpty rest || isLast name ->
            List.map entry (untitled [ first ]), rest
        | _ -> fail $"表の後ろに別の中身があります: %s{name}"

    let status, statusRest = table "ステータス(初期値/MAX値)"
    let equipment, equipmentRest = table "装備(装着可能Lv)"

    { Portrait = portrait
      Profile = profile
      Introduction = find "基本情報"
      Appearances = find "登場シナリオ"
      Acquisition = find "入手方法"
      Status = status
      Equipment = equipment
      Supplement = statusRest @ equipmentRest }

let rec private category (level: int) (section: Article.Section) : SkillCategory =
    if section.Level <> level then
        fail "区分の見出しの深さが飛んでいます"

    { Title = section.Title
      Skills = groups section.Body |> List.map chart
      Children = List.map (category (level + 1)) section.Children }

/// 節の直下の見出しの深さ。節はh2で分けるので、その中の区分はh3から始まる。
let private subsectionLevel = 3

let private skills (body: Block list) : SkillCategory list =
    let read = outline body

    if not (List.isEmpty read.Preamble) then
        fail "区分の見出しより前に中身があります"

    List.map (category subsectionLevel) read.Sections

/// 名前の小見出しから始まる節を、名前と画像と、残りの小見出しへ分ける。
let private named (body: Block list) : Inline list * Block list * (Inline list * Block list) list =
    match chunks body with
    | [], (name, icon) :: rest -> name, icon, rest
    | _ -> fail "名前の小見出しから始まっていません"

let private weapon (body: Block list) : Weapon =
    match named body with
    | name, icon, [ detail, description; limit, tables ] when
        label detail = "詳細" && label limit = "限界突破"
        ->
        match groups tables with
        | [ { Title = None; Items = limitBreak }; { Title = None; Items = growth } ] ->
            { Name = name
              Icon = icon
              Description = description
              LimitBreak = List.map entry limitBreak
              Growth = List.map row growth }
        | _ -> fail "限界突破と成長の2つの表ではありません"
    | _ -> fail "名前・詳細・限界突破の並びではありません"

let private gear (body: Block list) : Gear =
    match named body with
    | name, icon, [ detail, description; basicLabel, effects; upgradeLabel, upgrades ] when
        label detail = "詳細" && label basicLabel = "基本情報" && label upgradeLabel = "強化"
        ->
        { Name = name
          Icon = icon
          Description = description
          Effects = List.map row (untitled effects)
          Upgrades = List.map row (untitled upgrades) }
    | _ -> fail "名前・詳細・基本情報・強化の並びではありません"

/// 先頭に続く表を、題名を見出しとして持つものも含めて、まとまりの並びとして読む。
/// 題名は表の題名のセルから来ることも、表の手前の見出しから来ることもあり、
/// どちらも書き出せば同じ見出しになるので、見出しは次のまとまりの題名として読む。
/// 表ではないブロックからは後ろの注記として返す。
let private leadingGroups (blocks: Block list) : ItemGroup list * Block list =
    let rec read
        (pending: string option)
        (collected: ItemGroup list)
        (blocks: Block list)
        : ItemGroup list * Block list =
        match blocks, pending with
        | Heading(level, title) :: (Items _ :: _ as rest), None when level = subsectionLevel ->
            read (Some((plainText title).Trim())) collected rest
        | Items [ { Title = None } as group ] :: rest, Some title ->
            read None ({ group with Title = Some title } :: collected) rest
        | Items groups :: rest, None -> read None (List.rev groups @ collected) rest
        | _, Some _ -> fail "見出しの下が1つの表ではありません"
        | _, None -> List.rev collected, blocks

    read None [] blocks

/// 注記として持つブロック。見出しや表が残っていれば、読み残しなので読まない。
let private notes (blocks: Block list) : Block list =
    blocks
    |> List.iter (fun block ->
        match block with
        | Heading _
        | Items _ -> fail "注記の中に見出しか表があります"
        | _ -> ())

    blocks

let private potential (body: Block list) : Potential =
    match leadingGroups body with
    | [ { Title = None; Items = growth } ], rest ->
        { Growth = List.map row growth
          Materials = []
          Notes = notes rest }
    | [ { Title = None; Items = growth }
        { Title = Some "必要素材"
          Items = materials } ],
      rest ->
        { Growth = List.map row growth
          Materials = List.map row materials
          Notes = notes rest }
    | _ -> fail "成長と必要素材の表の並びではありません"

let private bondBonus (body: Block list) : BondBonus =
    match body with
    | Items [ { Title = None; Items = ranks } ] :: rest ->
        let totals, rest =
            match rest with
            | Heading(level, title) :: Items totals :: rest when
                level = subsectionLevel && label title = "ボーナス合計"
                ->
                List.map chart totals, rest
            | _ -> [], rest

        { Ranks = List.map row ranks
          Totals = totals
          Notes = notes rest }
    | _ -> fail "ランクごとのボーナスの表から始まっていません"

let private bondStory (body: Block list) : BondStory =
    match body with
    | Items [ { Title = None; Items = episodes } ] :: lobby ->
        { Episodes = List.map row episodes
          Lobby = lobby }
    | _ -> fail "エピソードの一覧の表から始まっていません"

let private voice (body: Block list) : VoiceGroup list =
    let lines (blocks: Block list) : Entry list = List.map entry (untitled blocks)

    let head, parts = chunks body

    (if List.isEmpty head then
         []
     else
         [ { Title = None; Lines = lines head } ])
    @ (parts
       |> List.map (fun (title, blocks) ->
           { Title = Some title
             Lines = lines blocks }))

/// 節の中身を読む。
let private content (title: string) (body: Block list) : Content =
    let hasTabs =
        body
        |> List.exists (fun block ->
            match block with
            | Tabs _ -> true
            | _ -> false)

    let attempt (read: Block list -> Content) : Content =
        try
            read body
        with Unreadable reason ->
            Other(body, Some reason)

    // タブで中身を切り替える節は、同じ形の中身を姿ごとに二重に持つ。
    // 2ページしか無いので専用の型では読まず、汎用の木のまま持つ。
    if hasTabs then
        Other(body, None)
    else
        match title with
        | "基本情報" -> attempt (basic >> Basic)
        | "スキル" -> attempt (skills >> Skills)
        | "固有武器" -> attempt (weapon >> UniqueWeapon)
        | "愛用品" -> attempt (gear >> FavoriteItem)
        | "能力解放" -> attempt (potential >> Potential)
        | "絆ランクボーナス" -> attempt (bondBonus >> BondBonus)
        | "絆ストーリー" -> attempt (bondStory >> BondStory)
        | "ボイス" -> attempt (voice >> Voice)
        | "小ネタ" -> Trivia body
        // ボイス集やカイテンジャーのメンバーごとの節のように、専用の型を用意していない節。
        | _ -> Other(body, None)

/// 節を分けている見出しの深さ。
let private sectionLevel = 2

/// 記事を生徒個別ページとして読む。
let read (article: Article) : Character =
    let read = outline article.Blocks

    let sections, stray =
        read.Sections |> List.partition (fun section -> section.Level = sectionLevel)

    { Preamble = read.Preamble @ List.collect sectionBlocks stray
      Sections =
        sections
        |> List.map (fun section ->
            { Title = section.Title
              Content =
                content
                    ((plainText section.Title).Trim())
                    (section.Body @ List.collect sectionBlocks section.Children) })
      Footnotes = article.Footnotes }
