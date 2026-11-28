module BluePrompt.Test.HtmlSpec

open Falco.Markup
open Xunit
open BluePrompt.Test.HtmlFixture

/// 本文の中身を汎用の読み方で読んで、Markdownへ書き出す。
/// 読んだ構造を書き出した形で確かめることで、事例を読みやすく保つ。
let private markdown (content: XmlNode list) : string =
    let document = parseBody content

    BluePrompt.MarkdownWriter.render
        { Blocks = BluePrompt.Html.blocks BluePrompt.Html.plain (document.QuerySelector "#body")
          Footnotes = [] }

[<Fact>]
let ``見出しと段落と箇条書きを読む`` () =
    let content =
        [ Text.h2 "小ネタ"
          Text.p "本文"
          Elem.ul
              []
              [ Elem.li [] [ Text.raw "親"; Elem.ul [] [ Elem.li [] [ Text.raw "子" ] ] ]
                Elem.li [] [ Text.raw "次" ] ] ]

    Assert.Equal("## 小ネタ\n\n本文\n\n- 親\n  - 子\n- 次\n", markdown content)

[<Fact>]
let ``ブロックの外に置かれた文字とインライン要素は段落にまとまる`` () =
    // wikiruの本文は段落の要素を使わず、文字と強調をdivへ直接並べることが多い。
    let content =
        [ Text.raw "前の文"
          Elem.strong [] [ Text.raw "強調" ]
          Text.h3 "見出し"
          Text.raw "後の文" ]

    Assert.Equal("前の文**強調**\n\n### 見出し\n\n後の文\n", markdown content)

[<Fact>]
let ``空白だけの段落は落ちる`` () =
    // ノーブレークスペースだけの段落は、残すと見出しの中身として数えられてしまう。
    let content = [ Text.p "&nbsp;"; Text.p "本文" ]
    Assert.Equal("本文\n", markdown content)

[<Fact>]
let ``強調の端の空白は外へ出る`` () =
    // 記号の内側に空白があると、Markdownでは強調として読まれない。
    let content =
        [ Elem.p [] [ Text.raw "前"; Elem.strong [] [ Text.raw " 強調 " ]; Text.raw "後" ] ]

    Assert.Equal("前 **強調** 後\n", markdown content)

[<Fact>]
let ``リンクは取得元のURLを基準に解決される`` () =
    let content =
        [ Elem.p
              []
              [ Elem.a [ Attr.href "./?other" ] [ Text.raw "サイト内" ]
                Elem.a [ Attr.href "https://example.com/post" ] [ Text.raw "外部" ] ] ]

    Assert.Equal(
        "[サイト内](https://bluearchive.wikiru.jp/?other)[外部](https://example.com/post)\n",
        markdown content
    )

[<Fact>]
let ``hrefを持たないリンクは中身だけが残る`` () =
    let content = [ Elem.p [] [ Elem.a [ Attr.name "anchor" ] [ Text.raw "文字" ] ] ]
    Assert.Equal("文字\n", markdown content)

[<Fact>]
let ``画像は遅延読み込みの属性を優先して絶対URLへ解決される`` () =
    // srcには遅延読み込みの間だけ表示する代わりの画像が入っている。
    let content =
        [ Elem.p
              []
              [ Elem.img
                    [ Attr.class' "lazyload"
                      Attr.create "data-src" "attach2/icon.png"
                      Attr.src "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP"
                      Attr.alt "素材名" ] ] ]

    Assert.Equal("![素材名](https://bluearchive.wikiru.jp/attach2/icon.png)\n", markdown content)

[<Fact>]
let ``解決できない画像は落ちる`` () =
    let content =
        [ Elem.p
              []
              [ Text.raw "本文"
                Elem.img [ Attr.src "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP" ] ] ]

    Assert.Equal("本文\n", markdown content)

[<Fact>]
let ``動画はURLのリンクになる`` () =
    let content =
        [ Elem.p [] [ Elem.video [ Attr.src "https://example.com/movie.mp4" ] [] ] ]

    Assert.Equal("<https://example.com/movie.mp4>\n", markdown content)

[<Fact>]
let ``取り消し線と引用と整形済みテキストを読む`` () =
    let content =
        [ Elem.p [] [ Elem.del [] [ Text.raw "茶化し" ] ]
          Elem.blockquote [] [ Text.p "引用" ]
          Elem.pre [] [ Text.raw "整形済み\n  2行目" ] ]

    Assert.Equal("~~茶化し~~\n\n> 引用\n\n```\n整形済み\n  2行目\n```\n", markdown content)

[<Fact>]
let ``スクリプトとスタイルの中身は本文にならない`` () =
    let content =
        [ Elem.script [] [ Text.raw "var x = 1;" ]
          Elem.style [] [ Text.raw "p { color: red }" ]
          Text.p "本文" ]

    Assert.Equal("本文\n", markdown content)

[<Fact>]
let ``隣り合う同じ強調は1つにまとまる`` () =
    // 別々に書くと記号が連なって強調として読めなくなる。
    let content =
        [ Elem.p [] [ Elem.strong [] [ Text.raw "やってる事は" ]; Elem.strong [] [ Text.raw "悪行" ] ] ]

    Assert.Equal("**やってる事は悪行**\n", markdown content)
