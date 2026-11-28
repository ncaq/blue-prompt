module BluePrompt.Test.ListingSpec

open Falco.Markup
open Xunit
open BluePrompt.Test.HtmlFixture

/// 本文の表を読み解いて、Markdownへ書き出す。
let private markdown (content: XmlNode list) : string =
    let document = parseBody content

    BluePrompt.MarkdownWriter.render
        { Blocks =
            BluePrompt.Html.blocks BluePrompt.Html.plain (document.QuerySelector "#body")
            |> BluePrompt.Listing.analyze
          Footnotes = [] }

let private th (text: string) : XmlNode = Elem.th [] [ Text.raw text ]
let private td (text: string) : XmlNode = Elem.td [] [ Text.raw text ]

[<Fact>]
let ``キーと値の表は項目の箇条書きになる`` () =
    let content =
        [ Elem.table [] [ Elem.tr [] [ th "名前"; td "ユウカ" ]; Elem.tr [] [ th "学園"; td "ミレニアム" ] ] ]

    Assert.Equal("- 名前: ユウカ\n- 学園: ミレニアム\n", markdown content)

[<Fact>]
let ``見出し行を持つ表は列名を添えた項目になる`` () =
    let content =
        [ Elem.table
              []
              [ Elem.tr [] [ th "Lv"; th "コスト"; th "効果" ]
                Elem.tr [] [ td "1"; td "3"; td "攻撃" ]
                Elem.tr [] [ td "2"; td "3"; td "強い攻撃" ] ] ]

    Assert.Equal("- Lv: 1, コスト: 3, 効果: 攻撃\n- Lv: 2, コスト: 3, 効果: 強い攻撃\n", markdown content)

[<Fact>]
let ``縦に結合したセルは下の行へ引き継がれる`` () =
    let content =
        [ Elem.table
              []
              [ Elem.tr [] [ th "区分"; th "場面"; th "セリフ" ]
                Elem.tr [] [ Elem.td [ Attr.rowspan "2" ] [ Text.raw "通常" ]; td "朝"; td "おはよう" ]
                Elem.tr [] [ td "昼"; td "こんにちは" ] ] ]

    Assert.Equal("- 区分: 通常, 場面: 朝, セリフ: おはよう\n- 区分: 通常, 場面: 昼, セリフ: こんにちは\n", markdown content)

[<Fact>]
let ``表を横断する見出しセルの行は小見出しになる`` () =
    let content =
        [ Elem.table
              []
              [ Elem.tr [] [ th "名前"; td "ユウカ" ]
                Elem.tr [] [ Elem.th [ Attr.colspan "2" ] [ Text.raw "入手方法" ] ]
                Elem.tr [] [ Elem.td [ Attr.colspan "2" ] [ Text.raw "通常募集" ] ] ] ]

    Assert.Equal("- 名前: ユウカ\n\n**入手方法**\n\n通常募集\n", markdown content)

[<Fact>]
let ``セルの中の改行は読点で繋がる`` () =
    // セルの中の改行は表示幅のための折り返しで、行の区切りとしては残さない。
    let cell = Elem.td [] [ Text.raw "セミナー"; Elem.br []; Text.raw "会計" ]
    let content = [ Elem.table [] [ Elem.tr [] [ th "所属"; cell ] ] ]

    Assert.Equal("- 所属: セミナー、会計\n", markdown content)

[<Fact>]
let ``画像の行と個数の行は1行へ畳まれる`` () =
    // 必要素材の表は素材の画像の行と個数の行を縦に重ね、Lvを縦の結合で両方へ掛けている。
    let image (name: string) : XmlNode =
        Elem.td [] [ Elem.img [ Attr.src $"https://example.com/%s{name}.png"; Attr.alt name ] ]

    let level = Elem.td [ Attr.rowspan "2" ] [ Text.raw "1-5" ]

    let content =
        [ Elem.table
              []
              [ Elem.tr [] [ th "Lv"; Elem.th [ Attr.colspan "2" ] [ Text.raw "素材" ] ]
                Elem.tr [] [ level; image "a"; image "b" ]
                Elem.tr [] [ td "x2"; td "x10" ] ] ]

    Assert.Equal(
        "- Lv: 1-5, 素材: ![a](https://example.com/a.png) x2, ![b](https://example.com/b.png) x10\n",
        markdown content
    )

[<Fact>]
let ``縦の結合で繋がっていない画像の行と文字の行は畳まれない`` () =
    // 無関係な2行がたまたま画像の行と文字の行だっただけの時に、項目を混ぜない。
    let image = Elem.img [ Attr.src "https://example.com/a.png"; Attr.alt "a" ]

    let content =
        [ Elem.table
              []
              [ Elem.tr [] [ td "立ち絵"; Elem.td [] [ image ] ]
                Elem.tr [] [ td "名前"; td "ユウカ" ] ] ]

    Assert.Equal("- 立ち絵: ![a](https://example.com/a.png)\n- 名前: ユウカ\n", markdown content)

[<Fact>]
let ``空のセルだけの行と列は落ちる`` () =
    let content =
        [ Elem.table
              []
              [ Elem.tr [] [ th "名前"; td "ユウカ"; td "" ]
                Elem.tr [] [ td ""; td ""; td "" ]
                Elem.tr [] [ th "学園"; td "ミレニアム"; td "" ] ] ]

    Assert.Equal("- 名前: ユウカ\n- 学園: ミレニアム\n", markdown content)

[<Fact>]
let ``値が空欄の項目はキーだけが残る`` () =
    let content =
        [ Elem.table [] [ Elem.tr [] [ th "名前"; td "連邦生徒会長" ]; Elem.tr [] [ th "フルネーム"; td "" ] ] ]

    Assert.Equal("- 名前: 連邦生徒会長\n- フルネーム\n", markdown content)
