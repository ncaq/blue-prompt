module BluePrompt.Test.CharacterSpec

open Xunit
open BluePrompt.Article
open BluePrompt.Character

let private cell (text: string) : CellContent =
    { Inlines = [ Text text ]; Blocks = [] }

/// 「キー: 値」の項目。
let private pair (key: string) (value: string) : Part list =
    [ Key(cell key); Value("", cell value) ]

/// 題名の無い1つの表。
let private table (items: Part list list) : Block =
    Items [ { Title = None; Items = items } ]

let private heading (level: int) (title: string) : Block = Heading(level, [ Text title ])

let private caption (title: string) : Block = Caption [ Text title ]

let private paragraph (text: string) : Block = Paragraph [ Text text ]

let private articleOf (blocks: Block list) : Article = { Blocks = blocks; Footnotes = [] }

/// 生徒個別ページの基本情報の節。
let private basicSection: Block list =
    [ heading 2 "基本情報"
      table
          [ [ Value(
                  "画像",
                  { Inlines = [ Image("立ち絵", System.Uri "https://example.com/a.png") ]
                    Blocks = [] }
              ) ]
            pair "名前" "ユウカ"
            [ Key(cell "CV") ] ]
      caption "基本情報"
      paragraph "紹介文"
      caption "入手方法"
      paragraph "通常募集"
      caption "ステータス(初期値/MAX値)"
      table [ pair "HP" "3146/47611" ]
      caption "装備(装着可能Lv)"
      table [ pair "Lv1" "シューズ" ] ]

/// 読んだ節の中身を1つだけ取り出す。
let private onlyContent (blocks: Block list) : Content =
    match (BluePrompt.CharacterReader.read (articleOf blocks)).Sections with
    | [ section ] -> section.Content
    | sections -> failwith $"節が1つではありません: %d{sections.Length}"

/// 読んで書き戻した記事をMarkdownにする。
let private roundTrip (blocks: Block list) : string =
    articleOf blocks
    |> BluePrompt.CharacterReader.read
    |> BluePrompt.CharacterWriter.toArticle
    |> BluePrompt.MarkdownWriter.render

[<Fact>]
let ``基本情報はプロフィールと小見出しごとの部分へ読まれる`` () =
    match onlyContent basicSection with
    | Basic basic ->
        Assert.True basic.Portrait.IsSome

        Assert.Equal<Entry list>(
            [ { Key = [ Text "名前" ]
                Value = [ Text "ユウカ" ] }
              // 値が空欄の項目は、値が空の並びになる。
              { Key = [ Text "CV" ]; Value = [] } ],
            basic.Profile
        )

        Assert.Equal<Block list>([ paragraph "紹介文" ], basic.Introduction)
        Assert.Equal<Block list>([ paragraph "通常募集" ], basic.Acquisition)

        Assert.Equal<Entry list>(
            [ { Key = [ Text "HP" ]
                Value = [ Text "3146/47611" ] } ],
            basic.Status
        )

        Assert.Equal<Entry list>(
            [ { Key = [ Text "Lv1" ]
                Value = [ Text "シューズ" ] } ],
            basic.Equipment
        )
    | content -> failwith $"基本情報として読めていません: %A{content}"

[<Fact>]
let ``専用の型で読んだ節は書き戻しても同じ文面になる`` () =
    // 小見出しの括弧の表記だけは、全角へ揃える。
    let expected =
        (BluePrompt.MarkdownWriter.render (articleOf basicSection))
            .Replace("装備(装着可能Lv)", "装備（装着可能Lv）")

    Assert.Equal(expected, roundTrip basicSection)

[<Fact>]
let ``知らない小見出しを持つ基本情報は理由を添えて汎用の木のまま持つ`` () =
    // 読めない所を黙って落とさないように、節ごと汎用の木のまま書き出す。
    let blocks = basicSection @ [ caption "隠しステータス"; paragraph "不明" ]

    match onlyContent blocks with
    | Other(body, Some reason) ->
        Assert.Equal<Block list>(List.tail blocks, body)
        Assert.Contains("隠しステータス", reason)
    | content -> failwith $"汎用の木のままになっていません: %A{content}"

    Assert.Equal(BluePrompt.MarkdownWriter.render (articleOf blocks), roundTrip blocks)

    Assert.Equal<(string * string) list>(
        [ "基本情報", "知らない小見出しがあります: 隠しステータス" ],
        unreadSections (BluePrompt.CharacterReader.read (articleOf blocks))
    )

[<Fact>]
let ``タブを持つ節と専用の型の無い節は理由を持たずに汎用の木のまま持つ`` () =
    let tabs = [ heading 2 "基本情報"; Tabs [ [ Text "防御型" ], List.tail basicSection ] ]

    let voices = [ heading 2 "ボイス集"; table [ pair "挨拶" "おはよう" ] ]

    for blocks in [ tabs; voices ] do
        match onlyContent blocks with
        | Other(body, None) -> Assert.Equal<Block list>(List.tail blocks, body)
        | content -> failwith $"理由の無い汎用の木になっていません: %A{content}"

[<Fact>]
let ``スキルは区分の木と題名付きの表へ読まれる`` () =
    let level (value: string) (effect: string) : Part list =
        [ Value("Lv", cell value); Value("効果", cell effect) ]

    let chart (title: string) : Block =
        Items
            [ { Title = Some title
                Items = [ level "1" "攻撃" ] } ]

    let blocks =
        [ heading 2 "スキル"
          heading 3 "EXスキル"
          heading 4 "防御型"
          chart "守る"
          heading 3 "ノーマルスキル"
          chart "撃つ" ]

    let rows: Row list =
        [ { Key = None
            Fields = [ "Lv", [ Text "1" ]; "効果", [ Text "攻撃" ] ] } ]

    Assert.Equal(
        Skills
            [ { Title = [ Text "EXスキル" ]
                Skills = []
                Children =
                  [ { Title = [ Text "防御型" ]
                      Skills = [ { Title = "守る"; Rows = rows } ]
                      Children = [] } ] }
              { Title = [ Text "ノーマルスキル" ]
                Skills = [ { Title = "撃つ"; Rows = rows } ]
                Children = [] } ],
        onlyContent blocks
    )

    Assert.Equal(BluePrompt.MarkdownWriter.render (articleOf blocks), roundTrip blocks)

[<Fact>]
let ``ボイスは小見出しごとの区分へ読まれる`` () =
    let blocks =
        [ heading 2 "ボイス"
          caption "一般"
          table [ pair "獲得" "条件はクリアされました。"; [ Key(cell "カフェ1") ] ] ]

    Assert.Equal(
        Voice
            [ { Title = Some [ Text "一般" ]
                Lines =
                  [ { Key = [ Text "獲得" ]
                      Value = [ Text "条件はクリアされました。" ] }
                    { Key = [ Text "カフェ1" ]; Value = [] } ] } ],
        onlyContent blocks
    )

    Assert.Equal(BluePrompt.MarkdownWriter.render (articleOf blocks), roundTrip blocks)

[<Fact>]
let ``role-playの参照には基本情報のプロフィールと紹介文までが残る`` () =
    // 入手方法・ステータス・装備は性能データで、role-playには使わない。
    let article =
        articleOf (basicSection @ [ heading 2 "ボイス"; table [ pair "獲得" "セリフ" ] ])
        |> BluePrompt.CharacterReader.read
        |> BluePrompt.Wikiru.rolePlayCharacter
        |> BluePrompt.CharacterWriter.toArticle

    Assert.Equal(
        "## 基本情報\n\n- 画像: ![立ち絵](https://example.com/a.png)\n- 名前: ユウカ\n- CV\n\n"
        + "**基本情報**\n\n紹介文\n\n## ボイス\n\n- 獲得: セリフ\n",
        BluePrompt.MarkdownWriter.render article
    )
