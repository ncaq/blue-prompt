module BluePrompt.Test.WikiruSpec

open Falco.Markup
open Xunit
open BluePrompt.Article
open BluePrompt.Test.HtmlFixture

/// 本文の中身から、ノイズを除いて整えた記事を読む。
let private articleOf (noise: string list) (content: XmlNode list) : Article =
    let document = parseBody content
    BluePrompt.WikiruDom.removeNoise noise document
    document |> BluePrompt.Wikiru.readArticle |> BluePrompt.Wikiru.prepare

/// 本文の中身を、記事用の読み方でMarkdownにする。
let private markdown (content: XmlNode list) : string =
    articleOf BluePrompt.WikiruDom.articleNoise content |> BluePrompt.Wikiru.render

/// 見出しと段落の並びだけの記事。節の規則を確かめる足場。
let private section (level: int) (title: string) (body: string list) : Block list =
    Heading(level, [ Text title ])
    :: List.map (fun text -> Paragraph [ Text text ]) body

/// Blockの並びを、節の後始末を通してMarkdownにする。
let private prepared (blocks: Block list) : string =
    { Blocks = blocks; Footnotes = [] }
    |> BluePrompt.Wikiru.prepare
    |> BluePrompt.Wikiru.render

[<Fact>]
let ``pageUriはページ名をパーセントエンコードしたURLを組み立てる`` () =
    Assert.Equal(
        "https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8",
        (BluePrompt.Wikiru.pageUri "キャラ呼称表").AbsoluteUri
    )

[<Fact>]
let ``本文が無いページはURLを添えて止まる`` () =
    // 構造の変更で本文を見失った時に、空のナレッジで上書きしない。
    let document = parseDocument [ Elem.div [ Attr.id "other" ] [ Text.p "本文" ] ]

    let error =
        Assert.Throws<BluePrompt.Wikiru.BodyNotFound>(fun () ->
            BluePrompt.Wikiru.readArticle document |> ignore)

    Assert.Equal(documentUrl, error.url)

[<Fact>]
let ``本文の外のヘッダとサイドバーは読まない`` () =
    let document =
        parseDocument
            [ Elem.div [ Attr.id "header" ] [ Text.raw "site header" ]
              Elem.div [ Attr.id "body" ] [ Text.h2 "本文"; Text.p "内容" ] ]

    Assert.Equal(
        "## 本文\n\n内容\n",
        document |> BluePrompt.Wikiru.readArticle |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``サイト内のリンクは外れて他のサイトへのリンクは残る`` () =
    let content =
        [ Text.h2 "小ネタ"
          Elem.p
              []
              [ Elem.a [ Attr.href "./?other" ] [ Text.raw "サイト内" ]
                Elem.a [ Attr.href "https://example.com/post" ] [ Text.raw "外部" ] ] ]

    Assert.Equal("## 小ネタ\n\nサイト内[外部](https://example.com/post)\n", markdown content)

[<Fact>]
let ``同じURLの画像を包むだけのリンクは外れる`` () =
    // 画像を原寸で開くためのリンクで、他のサイトの画像でもURLを繰り返すだけになる。
    let content =
        [ Text.h2 "小ネタ"
          Elem.p
              []
              [ Elem.a
                    [ Attr.href "https://example.com/a.png" ]
                    [ Elem.img [ Attr.src "https://example.com/a.png"; Attr.alt "図" ] ] ] ]

    Assert.Equal("## 小ネタ\n\n![図](https://example.com/a.png)\n", markdown content)

[<Fact>]
let ``中身の無いリンクはURLが表示文字になる`` () =
    // 埋め込みのツイートは中身を持たないリンクで、そのままでは何も指せない。
    let content =
        [ Text.h2 "小ネタ"
          Elem.blockquote [] [ Elem.a [ Attr.href "https://twitter.com/a/status/1" ] [] ] ]

    Assert.Equal("## 小ネタ\n\n> <https://twitter.com/a/status/1>\n", markdown content)

[<Fact>]
let ``本文の改行は1つなら行を2つ続けば段落を区切る`` () =
    // wikiruの本文は段落の要素を使わず、改行の数で行と段落を書き分けている。
    let content =
        [ Text.h2 "小ネタ"
          Text.raw "1行目"
          Elem.br []
          Text.raw "2行目"
          Elem.br []
          Elem.br []
          Text.raw "次の段落" ]

    Assert.Equal("## 小ネタ\n\n1行目\n2行目\n\n次の段落\n", markdown content)

[<Fact>]
let ``タブはラベルを小見出しにして中身を順に並べる`` () =
    // そのまま読むと、ラベルの箇条書きの後ろへ全てのタブの中身が区切り無く続いてしまう。
    let panel (index: int) (content: XmlNode list) : XmlNode =
        Elem.div [ Attr.id $"tabsabc-%d{index}" ] content

    let label (index: int) (text: string) : XmlNode =
        Elem.li [] [ Elem.a [ Attr.href $"#tabsabc-%d{index}" ] [ Text.raw text ] ]

    let content =
        [ Text.h2 "スキル"
          Elem.div
              [ Attr.id "tabsabc" ]
              [ Elem.ul [] [ label 1 "防御型"; label 2 "攻撃型" ]
                panel 1 [ Text.h4 "EXスキル"; Text.p "守る" ]
                panel 2 [ Text.h4 "EXスキル"; Text.p "攻める" ] ]
          Text.h2 "小ネタ"
          Text.p "本文" ]

    Assert.Equal(
        "## スキル\n\n**防御型**\n\n#### EXスキル\n\n守る\n\n**攻撃型**\n\n#### EXスキル\n\n攻める\n\n## 小ネタ\n\n本文\n",
        articleOf BluePrompt.WikiruDom.characterNoise content
        |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``未作成ページの名前は残り編集のボタンは取り除かれる`` () =
    // wikiruは個別ページがまだ無い名前を、編集フォームへのリンクとして表示する。
    let edit (content: XmlNode list) : XmlNode =
        Elem.a [ Attr.href "./?cmd=edit&amp;page=X" ] content

    let pencil = Elem.img [ Attr.src "https://example.com/edit.png" ]

    let document =
        parseBody
            [ Text.h2 "小ネタ"
              Elem.p
                  []
                  [ Text.raw "原画は"
                    edit [ Text.raw "Hwansang" ]
                    Text.raw "と"
                    Text.raw "キョウコ"
                    edit [ Text.raw "?" ]
                    edit [ Text.raw "EDIT"; pencil ] ] ]

    BluePrompt.WikiruDom.resolveEditLinks document

    Assert.Equal(
        "## 小ネタ\n\n原画はHwansangとキョウコ\n",
        document |> BluePrompt.Wikiru.readArticle |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``脚注は参照と定義として読まれる`` () =
    let note (label: string) : XmlNode =
        Elem.a [ Attr.class' "note_super"; Attr.href "#notefoot" ] [ Text.raw $"*%s{label}" ]

    let document =
        parseDocument
            [ Elem.div
                  [ Attr.id "body" ]
                  [ Text.h2 "小ネタ"
                    Elem.p [] [ Text.raw "拒否した事から"; note "4"; Text.raw "1955年に発足"; note "6" ] ]
              Elem.div
                  [ Attr.id "note" ]
                  [ note "4"
                    Elem.span [ Attr.class' "small" ] [ Text.raw "補足" ]
                    Elem.br []
                    note "5"
                    Elem.span [ Attr.class' "small" ] [ Text.raw "参照されない注" ]
                    Elem.br []
                    note "6"
                    Elem.span [ Attr.class' "small" ] [ Text.raw "別の補足" ]
                    Elem.br [] ] ]

    // 番号の直後に数字が続いても混ざらず、参照の無い定義は書き出さない。
    // 定義は1行ずつ並ぶ。
    Assert.Equal(
        "## 小ネタ\n\n拒否した事から[^4]1955年に発足[^6]\n\n[^4]: 補足\n[^6]: 別の補足\n",
        document
        |> BluePrompt.Wikiru.readArticle
        |> BluePrompt.Wikiru.prepare
        |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``コメント欄の節は丸ごと取り除かれる`` () =
    // コメントそのものは後から読み込まれるので残らないが、
    // 投稿のルールを畳んで置いているページがあり、見出しだけを消すと本文が残ってしまう。
    let blocks =
        section 2 "本文" [ "内容" ]
        @ section 2 "コメントフォーム" []
        @ section 3 "ルールについて" [ "違反投稿は削除します" ]
        @ section 2 "小ネタ" [ "続き" ]

    Assert.Equal("## 本文\n\n内容\n\n## 小ネタ\n\n続き\n", prepared blocks)

[<Fact>]
let ``小見出しのコメント欄は上位の節を巻き込まない`` () =
    let blocks =
        section 2 "小ネタ" [ "本文" ]
        @ section 3 "コメント" [ "ルール" ]
        @ section 2 "ボイス" [ "セリフ" ]

    Assert.Equal("## 小ネタ\n\n本文\n\n## ボイス\n\nセリフ\n", prepared blocks)

[<Fact>]
let ``最初の見出しより前のナビゲーションは切り落とされる`` () =
    let blocks = Paragraph [ Text "一覧 | 索引" ] :: section 2 "本文" [ "内容" ]
    Assert.Equal("## 本文\n\n内容\n", prepared blocks)

[<Fact>]
let ``見出しの無い文書はそのまま残る`` () =
    Assert.Equal("本文だけ\n", prepared [ Paragraph [ Text "本文だけ" ] ])

[<Fact>]
let ``中身を失った見出しは上位まで連鎖して取り除かれる`` () =
    let blocks =
        section 2 "ボイス" [ "セリフ" ]
        @ section 2 "小ネタ" []
        @ section 3 "立ち絵バリエーション" []
        @ section 4 "表情差分" []

    Assert.Equal("## ボイス\n\nセリフ\n", prepared blocks)

[<Fact>]
let ``中身を持つ小見出しがあれば上位の見出しも残る`` () =
    let blocks = section 2 "小ネタ" [] @ section 3 "使用している銃の元ネタ" [ "解説" ]
    Assert.Equal("## 小ネタ\n\n### 使用している銃の元ネタ\n\n解説\n", prepared blocks)

[<Fact>]
let ``編集者向けの欄の案内は取り除かれる`` () =
    // ページのテンプレートが置いていく案内で、生徒についての事実ではない。
    let blocks = section 2 "小ネタ" [ "キャラの性格やストーリーの紹介を書く欄です。"; "実際の小ネタ" ]
    Assert.Equal("## 小ネタ\n\n実際の小ネタ\n", prepared blocks)

[<Fact>]
let ``案内の前に別の文がある行は残る`` () =
    // 落とすのは行全体が案内である場合だけで、事実の文に案内が続く行を丸ごと落としてはいけない。
    let blocks = section 2 "小ネタ" [ "本文の文です。ここから先を書く欄です。" ]
    Assert.Equal("## 小ネタ\n\n本文の文です。ここから先を書く欄です。\n", prepared blocks)

[<Fact>]
let ``区切り文字だけになった行は取り除かれる`` () =
    // リンクを「/」で並べたナビゲーションは、並べた要素が落ちると区切り文字だけの行として残る。
    let blocks = section 2 "小ネタ" [ "/ /"; "本文" ]
    Assert.Equal("## 小ネタ\n\n本文\n", prepared blocks)

[<Fact>]
let ``立ち絵に添えられた全体画像の案内は読点を残さず取り除かれる`` () =
    // 縮小画像を包む全体画像へのリンクの説明で、リンクを外した後は辿る先が無い。
    // 表を読み解く前に行ごと落とすので、繋ぎの読点が跡として残らない。
    let content =
        [ Text.h2 "基本情報"
          Elem.table
              []
              [ Elem.tr
                    []
                    [ Elem.th [] [ Text.raw "画像" ]
                      Elem.td
                          []
                          [ Elem.img [ Attr.src "https://example.com/a.png"; Attr.alt "a" ]
                            Elem.br []
                            Text.raw "クリックで全体画像" ] ]
                Elem.tr [] [ Elem.th [] [ Text.raw "名前" ]; Elem.td [] [ Text.raw "ヒナ" ] ] ] ]

    Assert.Equal("## 基本情報\n\n- 画像: ![a](https://example.com/a.png)\n- 名前: ヒナ\n", markdown content)

[<Fact>]
let ``生徒ページの組は折りたたみの中身を残し開閉UIだけ取り除く`` () =
    // 生徒ページでは絆ストーリー一覧やボイスなどの本文が折りたたみの中に入っている。
    let content =
        [ Text.h2 "ボイス"
          Elem.div
              [ Attr.class' "rgn-container" ]
              [ Elem.span [ Attr.class' "rgn-button" ] [ Text.raw "+" ]
                Elem.span [ Attr.class' "rgn-description" ] [ Text.raw "クリックで展開" ]
                Elem.p [] [ Text.raw "折りたたみの本文" ] ] ]

    Assert.Equal(
        "## ボイス\n\n折りたたみの本文\n",
        articleOf BluePrompt.WikiruDom.characterNoise content
        |> BluePrompt.Wikiru.render
    )

    // 記事用の組では折りたたみごと落ちて、節も中身を失う。
    Assert.Equal("", markdown content)

[<Fact>]
let ``role-play用の組は取り消し線を中身ごと取り除く`` () =
    // 小ネタの取り消し線は編集者のツッコミで、演じる人格へ混ざると困る。
    let content =
        [ Text.h2 "小ネタ"
          Elem.p [] [ Elem.del [] [ Text.raw "もはやオカン" ]; Text.raw "世話を焼いている" ] ]

    Assert.Equal(
        "## 小ネタ\n\n世話を焼いている\n",
        articleOf BluePrompt.WikiruDom.rolePlayNoise content |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``どの組もスクリプトとコメント欄の除去を共有する`` () =
    // DOMを直接読む経路はMarkdown化固有の除去を持たないだけで、ノイズの除去は同じ集合を共有する。
    // ここが乖離すると片方の経路だけスクリプト片や投稿フォームの除去が漏れる。
    for noise in
        [ BluePrompt.WikiruDom.articleNoise
          BluePrompt.WikiruDom.characterNoise
          BluePrompt.WikiruDom.rolePlayNoise ] do
        Assert.All(
            BluePrompt.WikiruDom.structuredNoise,
            fun selector -> Assert.Contains(selector, noise)
        )

    Assert.Contains("script", BluePrompt.WikiruDom.structuredNoise)
    Assert.Contains(".pcomment", BluePrompt.WikiruDom.structuredNoise)

/// プロフィールの表。フルネームのセルの中身を受け取る。
let private profile (fullName: XmlNode list) : XmlNode =
    Elem.table
        []
        [ Elem.tr
              []
              [ Elem.th [] [ Text.raw "画像" ]
                Elem.td [] [ Elem.img [ Attr.src "https://example.com/a.png"; Attr.alt "立ち絵" ] ] ]
          Elem.tr [] [ Elem.th [] [ Text.raw "フルネーム" ]; Elem.td [] fullName ] ]

/// ふりがなをruby要素で持つフルネーム。
let private rubyName (family: string) (reading: string) (given: string) : XmlNode list =
    [ Elem.create "ruby" [] [ Text.raw family; Elem.create "rt" [] [ Text.raw reading ] ]
      Text.raw given ]

/// プロフィールと、追加の節を持つ生徒ページの記事。
let private studentArticle (fullName: XmlNode list) (rest: XmlNode list) : Article =
    articleOf BluePrompt.WikiruDom.characterNoise ([ Text.h2 "基本情報"; profile fullName ] @ rest)

[<Fact>]
let ``フルネームはふりがなを捨てて読まれる`` () =
    Assert.Equal(
        Some "早瀬ユウカ",
        BluePrompt.Wikiru.fullName (studentArticle (rubyName "早瀬" "はやせ" "ユウカ") [])
    )

[<Fact>]
let ``括弧で手書きされたふりがなも落ちる`` () =
    Assert.Equal(
        Some "清澄アキラ",
        BluePrompt.Wikiru.fullName (studentArticle [ Text.raw "清澄(きよすみ)アキラ" ] [])
    )

[<Fact>]
let ``フルネームが空欄か項目が無ければ読めない`` () =
    Assert.Equal(None, BluePrompt.Wikiru.fullName (studentArticle [] []))

    Assert.Equal(
        None,
        BluePrompt.Wikiru.fullName (
            articleOf BluePrompt.WikiruDom.characterNoise [ Text.h2 "基本情報" ]
        )
    )

[<Fact>]
let ``ネタバレ隠しのフォーム部品があってもプロフィールは項目として読める`` () =
    // アキラの部活のセルはネタバレ隠しプラグインのinputとlabelを含む。
    let spoiler =
        Elem.span
            [ Attr.class' "plugin-spoiler" ]
            [ Elem.input [ Attr.class' "spoiler-check"; Attr.type' "checkbox" ]
              Elem.label [ Attr.class' "spoiler-overlay" ] []
              Text.raw "寮監隊" ]

    let content =
        [ Text.h2 "基本情報"
          Elem.table
              []
              [ Elem.tr [] [ Elem.th [] [ Text.raw "名前" ]; Elem.td [] [ Text.raw "アキラ" ] ]
                Elem.tr [] [ Elem.th [] [ Text.raw "部活" ]; Elem.td [] [ spoiler ] ] ] ]

    Assert.Equal("## 基本情報\n\n- 名前: アキラ\n- 部活: 寮監隊\n", markdown content)

[<Fact>]
let ``familyNamedPageNameは衣装の接尾辞を残したまま姓を前へ置く`` () =
    Assert.Equal("栗村アイリ（バンド）", BluePrompt.Wikiru.familyNamedPageName "アイリ（バンド）" (Some "栗村アイリ"))

[<Fact>]
let ``familyNamedPageNameは姓を持たない生徒に何も足さない`` () =
    // プロフィールのフルネームが名前だけのケイのような生徒でも、名前を二重にしない。
    Assert.Equal("ケイ", BluePrompt.Wikiru.familyNamedPageName "ケイ" (Some "ケイ"))

[<Fact>]
let ``familyNamedPageNameはフルネームが姓名で揃うページ名をそのまま使う`` () =
    // ページ名の側が既に姓を含むコラボの生徒で、姓を二重にしない。
    Assert.Equal("御坂美琴", BluePrompt.Wikiru.familyNamedPageName "御坂美琴" (Some "御坂美琴"))

[<Fact>]
let ``familyNamedPageNameはフルネームが無ければ止まる`` () =
    let error =
        Assert.Throws<BluePrompt.Wikiru.StudentFullNameNotFound>(fun () ->
            BluePrompt.Wikiru.familyNamedPageName "ユウカ" None |> ignore)

    Assert.Equal("ユウカ", error.pageName)

[<Fact>]
let ``familyNamedPageNameはページ名と繋がらないフルネームで止まる`` () =
    // 姓を機械的に前へ置くと別人の名前を作ってしまうため、繋がらない時は書き出さない。
    let error =
        Assert.Throws<BluePrompt.Wikiru.StudentFullNameMismatch>(fun () ->
            BluePrompt.Wikiru.familyNamedPageName "ユウカ" (Some "早瀬ハルカ") |> ignore)

    Assert.Equal("ユウカ", error.pageName)
    Assert.Equal("早瀬ハルカ", error.fullName)

[<Fact>]
let ``unplayableDisplayNameはフルネームが無ければページ名をそのまま使う`` () =
    // 連邦生徒会長やアロナのように、姓名が明かされていない人物でも書き出せる。
    Assert.Equal("アロナ", BluePrompt.Wikiru.unplayableDisplayName "アロナ" None)

[<Fact>]
let ``unplayableDisplayNameはページ名と繋がらないフルネームで止まる`` () =
    // 姓を補えない時に黙ってページ名へ落とすと、読み方の崩れに気付けない。
    let error =
        Assert.Throws<BluePrompt.Wikiru.StudentFullNameMismatch>(fun () ->
            BluePrompt.Wikiru.unplayableDisplayName "リン" (Some "不知火カヤ") |> ignore)

    Assert.Equal("リン", error.pageName)

[<Fact>]
let ``selectSectionsはホワイトリストにあるh2の節だけを小見出しごと残す`` () =
    let article: Article =
        { Blocks =
            section 2 "基本情報" [ "名前" ]
            @ section 2 "運用考察" []
            @ section 3 "総評" [ "考察本文" ]
            @ section 2 "小ネタ" []
            @ section 3 "元ネタ" [ "解説" ]
          Footnotes = [] }

    Assert.Equal(
        "## 基本情報\n\n名前\n\n## 小ネタ\n\n### 元ネタ\n\n解説\n",
        article
        |> BluePrompt.Wikiru.selectSections BluePrompt.Wikiru.studentSectionTitles
        |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``節ごと落ちた参照の脚注の定義は書き出されない`` () =
    // 落ちた節だけが参照していた脚注の定義を残すと、宙に浮いた脚注になる。
    let article: Article =
        { Blocks =
            [ Heading(2, [ Text "基本情報" ])
              Paragraph [ Text "名前"; FootnoteReference "1" ]
              Heading(2, [ Text "運用考察" ])
              Paragraph [ Text "考察"; FootnoteReference "2" ] ]
          Footnotes =
            [ { Label = "1"; Content = [ Text "補足" ] }
              { Label = "2"
                Content = [ Text "考察の補足" ] } ] }

    Assert.Equal(
        "## 基本情報\n\n名前[^1]\n\n[^1]: 補足\n",
        article
        |> BluePrompt.Wikiru.selectSections [ "基本情報" ]
        |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``trimGameplayDetailsは入手方法の小見出しから次の節までを切り落とす`` () =
    // 基本情報の節の入手方法・ステータス・装備は性能データで、
    // role-playの参照にはプロフィールと紹介文とボイスだけが要る。
    let article: Article =
        { Blocks =
            [ Heading(2, [ Text "基本情報" ])
              Caption [ Text "基本情報" ]
              Paragraph [ Text "紹介文" ]
              Caption [ Text "入手方法" ]
              Paragraph [ Text "通常募集" ]
              Heading(3, [ Text "装備" ])
              Paragraph [ Text "帽子" ]
              Heading(2, [ Text "ボイス" ])
              Paragraph [ Text "セリフ" ] ]
          Footnotes = [] }

    Assert.Equal(
        "## 基本情報\n\n**基本情報**\n\n紹介文\n\n## ボイス\n\nセリフ\n",
        article |> BluePrompt.Wikiru.trimGameplayDetails |> BluePrompt.Wikiru.render
    )

[<Fact>]
let ``trimGameplayDetailsは入手方法が無ければ何も変えない`` () =
    let article: Article =
        { Blocks = section 2 "基本情報" [ "紹介文" ] @ section 2 "ボイス" [ "セリフ" ]
          Footnotes = [] }

    Assert.Equal(article, BluePrompt.Wikiru.trimGameplayDetails article)

[<Fact>]
let ``sectionTitlesはh2の見出しだけを現れる順に重複なく拾う`` () =
    // ページ側の事情で見出しが重複しても、「ボイス・ボイス」のような説明文にしない。
    let article: Article =
        { Blocks =
            section 2 "ボイス" [ "本文" ]
            @ section 3 "内訳" [ "本文" ]
            @ section 2 "基本情報" [ "本文" ]
            @ section 2 "ボイス" [ "本文" ]
          Footnotes = [] }

    Assert.Equal<string list>([ "ボイス"; "基本情報" ], BluePrompt.Wikiru.sectionTitles article)

[<Fact>]
let ``studentSkillMarkdownはフロントマターと出典とナレッジを含む`` () =
    let skill =
        BluePrompt.Wikiru.studentSkillMarkdown
            "character-yuuka"
            "ユウカ"
            (studentArticle (rubyName "早瀬" "はやせ" "ユウカ") [ Text.h2 "ボイス"; Text.p "セリフ" ])

    Assert.StartsWith("---\nname: character-yuuka\n", skill)
    // 説明はスキルの一覧としてモデルのコンテキストへ載るため、
    // 生徒ごとに変わるのが名前だけの短い1文であることを固定する。
    Assert.Contains("\ndescription: Facts about Blue Archive student 早瀬ユウカ.\n", skill)
    Assert.Contains("\nuser-invocable: false\n", skill)
    Assert.Contains("https://bluearchive.wikiru.jp/?%E3%83%A6%E3%82%A6%E3%82%AB", skill)
    // 愛用品の節を持たないページで、存在しない節を存在すると宣言しない。
    Assert.Contains("- セクションは基本情報・ボイスです\n", skill)
    Assert.Contains("## 基本情報\n\n- 画像: ![立ち絵](https://example.com/a.png)\n", skill)

[<Fact>]
let ``studentSkillMarkdownは節が1つも無ければ止まる`` () =
    // 何が載っているかを述べない壊れた説明のスキルを書き出さない。
    let article: Article =
        { Blocks = [ Paragraph [ Text "本文だけ" ] ]
          Footnotes = [] }

    let error =
        Assert.Throws<BluePrompt.Wikiru.StudentSectionNotFound>(fun () ->
            BluePrompt.Wikiru.studentSkillMarkdown "character-yuuka" "ユウカ" article |> ignore)

    Assert.Equal("ユウカ", error.pageName)

[<Fact>]
let ``unplayableSkillMarkdownの説明は登場人物として姓を補った名前を述べる`` () =
    // 未実装の生徒も生徒なので、wikiruのレアリティの表記のNPCとは呼ばない。
    let skill =
        BluePrompt.Wikiru.unplayableSkillMarkdown
            "character-rin"
            "リン"
            (studentArticle (rubyName "七神" "なながみ" "リン") [])

    Assert.StartsWith("---\nname: character-rin\n", skill)
    Assert.Contains("\ndescription: Facts about Blue Archive character 七神リン.\n", skill)
    Assert.Contains("『ブルーアーカイブ』の登場人物「リン」の", skill)
    Assert.DoesNotContain("NPC", skill)

[<Fact>]
let ``studentSectionTitlesは小ネタを残す`` () =
    // 小ネタはゲーム内外の事実のまとめで、事実には著作権が発生しないため、
    // 運用考察などの執筆者の考察とは扱いが異なる。
    // 文言の見直しで落ちるとナレッジから静かに消えるため、実リストへ確かめる。
    Assert.Contains("小ネタ", BluePrompt.Wikiru.studentSectionTitles)

[<Fact>]
let ``sourceHeaderはURLをそのまま使いページ名をデコードで復元する`` () =
    let header =
        BluePrompt.Wikiru.sourceHeader (
            System.Uri "https://bluearchive.wikiru.jp/?%E3%83%A6%E3%82%A6%E3%82%AB"
        )

    Assert.Contains("[ユウカ - ", header)
    Assert.Contains("(https://bluearchive.wikiru.jp/?%E3%83%A6%E3%82%A6%E3%82%AB)", header)

[<Fact>]
let ``sourceHeaderはクエリの無いURLではURL全体を表示名にする`` () =
    // ページ名を復元できないURLでも表記が空欄にならないようにする。
    let header =
        BluePrompt.Wikiru.sourceHeader (System.Uri "https://bluearchive.wikiru.jp/")

    Assert.Contains("[https://bluearchive.wikiru.jp/ - ", header)

[<Fact>]
let ``knowledgeHeaderは出典URLを含む`` () =
    Assert.Contains(
        "https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8",
        BluePrompt.Wikiru.knowledgeHeader "キャラ呼称表"
    )
