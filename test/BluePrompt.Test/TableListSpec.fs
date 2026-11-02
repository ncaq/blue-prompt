module BluePrompt.Test.TableListSpec

open System.Text.RegularExpressions
open System.Threading.Tasks
open Falco.Markup
open Xunit
open BluePrompt.Test.HtmlFixture

/// 本文を#contentのmainだけへ入れたHTML文書。
let private renderContentDocument (content: XmlNode list) : string =
    renderDocument [ Elem.main [ Attr.id "content" ] content ]

/// 平坦化の後に表を箇条書きへ置き換えるクエリ。
let private listQuery: BluePrompt.Extract.ContentQuery =
    { ContentSelectors = [ "#content" ]
      RemoveSelectors = []
      UnwrapInternalLinks = false
      ResolveImages = false
      FlattenTables = true
      TablesToLists = true }

let private extract (content: XmlNode list) : string =
    BluePrompt.Extract.contentHtml listQuery (renderContentDocument content)

[<Fact>]
let ``題名行と列名の行を持つ表は題名の見出しと列名付きの項目になる`` () =
    let html =
        extract
            [ Text.h3 "EXスキル"
              Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td
                              []
                              [ Elem.img
                                    [ Attr.src "https://example.com/icon.png"; Attr.alt "icon" ] ]
                          Elem.th [ Attr.colspan "2" ] [ Text.raw "Q.E.D" ] ]
                    Elem.tr
                        []
                        [ Elem.th [] [ Text.raw "Lv" ]
                          Elem.th [] [ Text.raw "コスト" ]
                          Elem.th [] [ Text.raw "効果" ] ]
                    Elem.tr
                        []
                        [ Elem.td [] [ Text.raw "1" ]
                          Elem.td [] [ Text.raw "3" ]
                          Elem.td [] [ Text.raw "治癒力の190%分のシールド" ] ] ] ]

    Assert.DoesNotContain("<table", html)
    // 題名は表の属する節の1つ下の見出しになる。
    Assert.Contains("<h4>Q.E.D</h4>", html)
    Assert.Contains("<li>Lv: 1, コスト: 3, 効果: 治癒力の190%分のシールド</li>", html)
    // 題名行のアイコンは名前に添えた飾りなので残らない。
    Assert.DoesNotContain("<img", html)

[<Fact>]
let ``題名が複数並ぶ表は題名ごとの節に分かれキーの列を繰り返す`` () =
    let html =
        extract
            [ Text.h3 "ノーマルスキル"
              Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td [] []
                          Elem.th [] [ Text.raw "I.F.F" ]
                          Elem.th [] [ Text.raw "I.F.F+" ] ]
                    Elem.tr
                        []
                        [ Elem.th [] [ Text.raw "Lv" ]
                          Elem.th [] [ Text.raw "効果" ]
                          Elem.th [] [ Text.raw "効果（愛用品T2）" ] ]
                    Elem.tr
                        []
                        [ Elem.td [] [ Text.raw "1" ]
                          Elem.td [] [ Text.raw "301%" ]
                          Elem.td [] [ Text.raw "329%" ] ] ] ]

    Assert.Contains("<h4>I.F.F</h4><ul><li>Lv: 1, 効果: 301%</li></ul>", html)
    Assert.Contains("<h4>I.F.F+</h4><ul><li>Lv: 1, 効果（愛用品T2）: 329%</li></ul>", html)

[<Fact>]
let ``見出し行の無い表は先頭のセルをキーにする`` () =
    // ボイスのラベルとセリフのようにtdだけの2列と、固有武器のようにthで始まる行の両方。
    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td [] [ Text.raw "獲得" ]; Elem.td [] [ Text.raw "条件はクリアされました。" ] ]
                    Elem.tr [] [ Elem.td [] [ Text.raw "カフェ1" ]; Elem.td [] [ Text.raw "あれ？" ] ] ]
              Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.th [] [ Text.raw "★2" ]; Elem.td [] [ Text.raw "パッシブスキルが変化" ] ] ] ]

    Assert.Contains("<ul><li>獲得: 条件はクリアされました。</li><li>カフェ1: あれ？</li></ul>", html)
    Assert.Contains("<ul><li>★2: パッシブスキルが変化</li></ul>", html)

[<Fact>]
let ``列名の行とキーの両方を持つ表は列名を値に添える`` () =
    // 能力解放のようにLvが列に、能力が行に並ぶ表。
    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.th [] []
                          Elem.th [] [ Text.raw "Lv.10" ]
                          Elem.th [] [ Text.raw "Lv.20" ] ]
                    Elem.tr
                        []
                        [ Elem.th [] [ Text.raw "最大HP" ]
                          Elem.td [] [ Text.raw "+906" ]
                          Elem.td [] [ Text.raw "+1813" ] ] ] ]

    Assert.Contains("<li>最大HP: Lv.10: +906, Lv.20: +1813</li>", html)

[<Fact>]
let ``立ち絵の列は独立した項目になり他の画像は行の値に残る`` () =
    // プロフィールの表は縦に結合された立ち絵の列と、見出しのセルと値が並ぶ。
    let portrait = Elem.img [ Attr.src "https://example.com/a.png"; Attr.alt "" ]
    let chibi = Elem.img [ Attr.src "https://example.com/b.png"; Attr.alt "" ]

    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.th [] [ Text.raw "画像" ]
                          Elem.th [ Attr.colspan "2" ] [ Text.raw "プロフィール" ] ]
                    Elem.tr
                        []
                        [ Elem.td [ Attr.rowspan "2" ] [ portrait ]
                          Elem.th [] [ Text.raw "名前" ]
                          Elem.td [] [ Text.raw "ユウカ" ] ]
                    Elem.tr [] [ Elem.th [] [ Text.raw "ちびキャラ" ]; Elem.td [] [ chibi ] ] ] ]

    Assert.Contains(
        """<ul><li>画像: <img src="https://example.com/a.png" alt=""></li>"""
        + """<li>名前: ユウカ</li>"""
        + """<li>ちびキャラ: <img src="https://example.com/b.png" alt=""></li></ul>""",
        html
    )

[<Fact>]
let ``説明の文字を持つ立ち絵の列も独立した項目になる`` () =
    // 立ち絵のセルが画像と「クリックで全体画像」の文字を持つページでは、
    // 縦に結合されたセルの複製が文字だけを引き継いで各行へ並ぶ。
    let portrait = Elem.img [ Attr.src "https://example.com/a.png"; Attr.alt "" ]

    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td
                              [ Attr.rowspan "2" ]
                              [ portrait; Elem.br []; Text.raw "クリックで全体画像" ]
                          Elem.th [] [ Text.raw "名前" ]
                          Elem.td [] [ Text.raw "ヒナ" ] ]
                    Elem.tr [] [ Elem.th [] [ Text.raw "学園" ]; Elem.td [] [ Text.raw "ゲヘナ" ] ] ] ]

    Assert.Contains(
        """<ul><li><img src="https://example.com/a.png" alt="">、クリックで全体画像</li>"""
        + """<li>名前: ヒナ</li><li>学園: ゲヘナ</li></ul>""",
        html
    )

[<Fact>]
let ``データ行が1行だけの表の画像は立ち絵ではなく行の値に残る`` () =
    // 立ち絵の列は縦に結合されたセルの展開で生まれるので、複製が1つも無ければ立ち絵ではない。
    let icon = Elem.img [ Attr.src "https://example.com/icon.png"; Attr.alt "素材" ]

    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr [] [ Elem.th [] [ Text.raw "Lv" ]; Elem.th [] [ Text.raw "素材" ] ]
                    Elem.tr [] [ Elem.td [] [ Text.raw "1-5" ]; Elem.td [] [ icon ] ] ] ]

    Assert.Contains(
        """<ul><li>Lv: 1-5, 素材: <img src="https://example.com/icon.png" alt="素材"></li></ul>""",
        html
    )

[<Fact>]
let ``表の途中の列名の行はそこから別の一覧として読む`` () =
    // 固有武器の表は限界突破の行の後に、Lvを列に並べたステータスの一覧が続く。
    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr [] [ Elem.th [] [ Text.raw "★2" ]; Elem.td [] [ Text.raw "変化" ] ]
                    Elem.tr
                        []
                        [ Elem.th [] []
                          Elem.th [] [ Text.raw "Lv.1" ]
                          Elem.th [] [ Text.raw "Lv.30" ] ]
                    Elem.tr
                        []
                        [ Elem.th [] [ Text.raw "攻撃力" ]
                          Elem.td [] [ Text.raw "43" ]
                          Elem.td [] [ Text.raw "156" ] ] ] ]

    Assert.Contains("<ul><li>★2: 変化</li></ul><ul><li>攻撃力: Lv.1: 43, Lv.30: 156</li></ul>", html)

[<Fact>]
let ``tdで書かれた画像の列の見出しは見出し行の判定を妨げない`` () =
    // 絆ランクボーナス合計の表は画像の列の見出しだけをtdで書き、
    // 対象の列の見出しは2つの見出し行に縦に結合されている。
    let icon = Elem.img [ Attr.src "https://example.com/icon.png"; Attr.alt "ユウカ" ]

    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td [ Attr.rowspan "2" ] [ Text.raw "_画像_" ]
                          Elem.th [ Attr.rowspan "2" ] [ Text.raw "対象" ]
                          Elem.th [ Attr.colspan "2" ] [ Text.raw "絆ランク20" ] ]
                    Elem.tr [] [ Elem.th [] [ Text.raw "最大HP" ]; Elem.th [] [ Text.raw "攻撃力" ] ]
                    Elem.tr
                        []
                        [ Elem.td [] [ icon ]
                          Elem.td [] [ Text.raw "ユウカ" ]
                          Elem.td [] [ Text.raw "1700" ]
                          Elem.td [] [ Text.raw "30" ] ]
                    Elem.tr
                        []
                        [ Elem.td [] [ icon ]
                          Elem.td [] [ Text.raw "ユウカ（体操服）" ]
                          Elem.td [] [ Text.raw "1045" ]
                          Elem.td [] [ Text.raw "0" ] ] ] ]

    let iconHtml = """<img src="https://example.com/icon.png" alt="ユウカ">"""

    Assert.Contains(
        $"""<h2>絆ランク20</h2><ul><li>%s{iconHtml}, 対象: ユウカ, 最大HP: 1700, 攻撃力: 30</li>"""
        + $"""<li>%s{iconHtml}, 対象: ユウカ（体操服）, 最大HP: 1045, 攻撃力: 0</li></ul>""",
        html
    )

[<Fact>]
let ``セルの両端の空白は区切りの前に残らない`` () =
    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td
                              []
                              [ Text.raw " "; Elem.strong [] [ Text.raw "合計" ]; Text.raw " " ]
                          Elem.td [] [ Text.raw "100" ] ] ] ]

    Assert.Contains("<li><strong>合計</strong>: 100</li>", html)

[<Fact>]
let ``見出しを含む表はレイアウトなので置き換えない`` () =
    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr [] [ Elem.td [] [ Text.h2 "本文の見出し"; Elem.p [] [ Text.raw "本文" ] ] ] ] ]

    Assert.Contains("<table", html)
    Assert.DoesNotContain("<ul", html)

[<Fact>]
let ``結合セルが残った表は平坦化を諦めた表なので置き換えない`` () =
    // 格子の予算を超えた表は結合セルを残したままになる。
    // 展開されていない格子を項目にすると列の対応が崩れるので、表のまま残す。
    // 平坦化の予算は抽出の設定から変えられないため、
    // 平坦化を通さずに結合セルを持つ表を直接TableListへ渡す。
    let html =
        renderContentDocument
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td [ Attr.rowspan "2" ] [ Text.raw "結合" ]
                          Elem.td [] [ Text.raw "a" ] ]
                    Elem.tr [] [ Elem.td [] [ Text.raw "b" ] ] ] ]

    use parsed = AngleSharp.Html.Parser.HtmlParser().ParseDocument html
    BluePrompt.TableList.convert parsed

    Assert.Contains("<table", parsed.Body.InnerHtml)
    Assert.DoesNotContain("<ul", parsed.Body.InnerHtml)

[<Fact>]
let ``題名行が2段ある表は題名を空白で繋いだ見出しになる`` () =
    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr [] [ Elem.td [] []; Elem.th [] [ Text.raw "上段" ] ]
                    Elem.tr [] [ Elem.td [] []; Elem.th [] [ Text.raw "下段" ] ]
                    Elem.tr [] [ Elem.th [] [ Text.raw "Lv" ]; Elem.th [] [ Text.raw "効果" ] ]
                    Elem.tr [] [ Elem.td [] [ Text.raw "1" ]; Elem.td [] [ Text.raw "値" ] ] ] ]

    Assert.Contains("<h2>上段 下段</h2><ul><li>Lv: 1, 効果: 値</li></ul>", html)

[<Fact>]
let ``題名の見出しは深さを使い切ると強調の段落になる`` () =
    let html =
        extract
            [ Text.h6 "最も深い節"
              Elem.table
                  []
                  [ Elem.tr [] [ Elem.th [] [ Text.raw "題名" ] ]
                    Elem.tr [] [ Elem.th [] [ Text.raw "列名" ] ]
                    Elem.tr [] [ Elem.td [] [ Text.raw "値" ] ] ] ]

    Assert.Contains("<p><strong>題名</strong></p><ul><li>列名: 値</li></ul>", html)
    Assert.DoesNotContain("<h7", html)

[<Fact>]
let ``入れ子の表は内側から箇条書きになる`` () =
    let html =
        extract
            [ Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.th [] [ Text.raw "外側" ]
                          Elem.td
                              []
                              [ Elem.table
                                    []
                                    [ Elem.tr
                                          []
                                          [ Elem.th [] [ Text.raw "内側" ]
                                            Elem.td [] [ Text.raw "値" ] ] ] ] ] ] ]

    Assert.DoesNotContain("<table", html)
    Assert.Contains("<li>外側: <ul><li>内側: 値</li></ul></li>", html)

[<Fact>]
let ``内側の表の題名が作る見出しで外側の表がレイアウト扱いにならない`` () =
    // 内側の表を先に置き換えると題名の見出しが外側のセルへ入るが、
    // それは置き換えが作った見出しで、外側の表がレイアウトであることを意味しない。
    let inner =
        Elem.table
            []
            [ Elem.tr [] [ Elem.th [] [ Text.raw "題名" ] ]
              Elem.tr [] [ Elem.th [] [ Text.raw "列名" ] ]
              Elem.tr [] [ Elem.td [] [ Text.raw "値" ] ] ]

    let html =
        extract
            [ Elem.table [] [ Elem.tr [] [ Elem.th [] [ Text.raw "外側" ]; Elem.td [] [ inner ] ] ] ]

    Assert.DoesNotContain("<table", html)
    Assert.Contains("<li>外側: <h2>題名</h2><ul><li>列名: 値</li></ul></li>", html)

[<Fact>]
let ``箇条書きにした表はpandocで見出しと項目のMarkdownになる`` () : Task =
    task {
        let html =
            extract
                [ Text.h3 "EXスキル"
                  Elem.table
                      []
                      [ Elem.tr
                            []
                            [ Elem.td [] []; Elem.th [ Attr.colspan "2" ] [ Text.raw "Q.E.D" ] ]
                        Elem.tr
                            []
                            [ Elem.th [] [ Text.raw "Lv" ]
                              Elem.th [] [ Text.raw "コスト" ]
                              Elem.th [] [ Text.raw "効果" ] ]
                        Elem.tr
                            []
                            [ Elem.td [] [ Text.raw "1" ]
                              Elem.td [] [ Text.raw "3" ]
                              Elem.td [] [ Text.raw "治癒力の190%分のシールド" ] ] ] ]

        let! markdown = BluePrompt.Pandoc.toMarkdown html
        Assert.Contains("\n#### Q.E.D\n", markdown)
        Assert.Contains("\n- Lv: 1, コスト: 3, 効果: 治癒力の190%分のシールド\n", markdown)
        // 表が残っているとパイプテーブルの整形で空白が膨らむ。
        Assert.DoesNotMatch(Regex "\\|", markdown)
    }
