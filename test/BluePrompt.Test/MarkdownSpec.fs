module BluePrompt.Test.MarkdownSpec

open System.Text
open Xunit
open BluePrompt.Markdown

/// 断片の本文を並べたもの。分割のされ方を1つの値として見比べるために使う。
let private texts (fragments: Fragment list) = fragments |> List.map _.Text

let private sample =
    """出典: どこかのページ

## 学校A

### 部活X

Xの説明。

### 部活Y

Yの説明。

## 学校B

Bの説明。
"""

[<Fact>]
let ``上限に収まる文書は分割されない`` () =
    let fragments = splitBySize 4096 sample

    Assert.Equal(1, List.length fragments)
    Assert.Equal(sample.Trim() + "\n", fragments.Head.Text)
    // 分割していないので見出しの並びはルートだけで空になる。
    Assert.Equal<string list>([], fragments.Head.Headings)

[<Fact>]
let ``上限を超えると見出しの単位まで降りて分割される`` () =
    // 学校の単位では収まらず部活の単位なら収まる大きさを選ぶ。
    let fragments = splitBySize 60 sample

    // 見出しの前にある文書全体の前書きも、どの見出しにも属さない断片として残る。
    Assert.Equal<string list list>(
        [ []; [ "学校A"; "部活X" ]; [ "学校A"; "部活Y" ]; [ "学校B" ] ],
        fragments |> List.map _.Headings
    )

[<Fact>]
let ``断片には祖先の見出しが前置される`` () =
    let fragments = splitBySize 60 sample

    // 部活Xの断片だけを読んでも、どの学校の何なのかが分かる。
    // 文書全体の前書きは前置しない。
    // ベクトル検索では、全ての断片が同じ書き出しになって似たベクトルへ寄り、
    // 本当に読ませたい実データのチャンクを検索結果から押し出すため。
    Assert.Equal("## 学校A\n### 部活X\n\nXの説明。\n", fragments[1].Text)

[<Fact>]
let ``見出しの直下にある本文は子とは別の断片になる`` () =
    let markdown =
        """# 章

章の前置き。

## 節1

節1の中身。

## 節2

節2の中身。
"""

    let fragments = splitBySize 40 markdown

    Assert.Equal<string list>(
        [ "# 章\n\n章の前置き。\n"; "# 章\n## 節1\n\n節1の中身。\n"; "# 章\n## 節2\n\n節2の中身。\n" ],
        texts fragments
    )

[<Fact>]
let ``コードブロックの中の見出しに見える行では分割されない`` () =
    let markdown =
        """## 節

```console
# これはコメントであって見出しではない
```

本文。
"""

    let fragments = splitBySize 10 markdown

    // 上限を大きく超えていても、これ以上分けられないので1つのままになる。
    Assert.Equal(1, List.length fragments)
    Assert.Contains("# これはコメントであって見出しではない", fragments.Head.Text)

[<Fact>]
let ``閉じたコードフェンスの後ろの見出しでは分割される`` () =
    // フェンスの中と外の見分けは行から行へ持ち回る状態なので、
    // 閉じフェンスで状態が戻らないと以降の見出しが本文として埋もれる。
    let markdown =
        """## 節1

```console
# これはコメントであって見出しではない
```

## 節2

節2の中身。
"""

    let fragments = splitBySize 10 markdown

    Assert.Equal<string list>(
        [ "## 節1\n\n```console\n# これはコメントであって見出しではない\n```\n"; "## 節2\n\n節2の中身。\n" ],
        texts fragments
    )

[<Fact>]
let ``分けられない大きな節は上限を超えたまま1つの断片になる`` () =
    let markdown = "## 節\n\n" + String.replicate 100 "長い本文。\n"

    let fragments = splitBySize 10 markdown

    Assert.Equal(1, List.length fragments)
    Assert.True(10 < Encoding.UTF8.GetByteCount fragments.Head.Text)

[<Fact>]
let ``見出しの深さが飛んでいても階層として読み解ける`` () =
    let markdown =
        """## 節

#### 深い項目

中身。
"""

    let root = parseSections markdown
    let section = List.exactlyOne root.Children

    Assert.Equal(Some "節", section.Heading)
    Assert.Equal(Some "深い項目", (List.exactlyOne section.Children).Heading)

/// 文書の直下の節の見出し。どの行が見出しとして読まれたのかを見比べるために使う。
let private topHeadings (markdown: string) : string list =
    (parseSections markdown).Children |> List.choose _.Heading

[<Fact>]
let ``閉じられていないコードフェンスは文書の末尾まで続く`` () =
    Assert.Equal<string list>([ "節" ], topHeadings "## 節\n\n```\n## フェンスの中\n")

[<Fact>]
let ``開いたフェンスより短いフェンスでは閉じない`` () =
    let markdown = "````\n```\n## フェンスの中\n````\n\n## 後ろ\n"
    Assert.Equal<string list>([ "後ろ" ], topHeadings markdown)

[<Fact>]
let ``チルダのフェンスはバッククォートでは閉じない`` () =
    let markdown = "~~~\n```\n## フェンスの中\n~~~\n\n## 後ろ\n"
    Assert.Equal<string list>([ "後ろ" ], topHeadings markdown)

[<Fact>]
let ``4つ以上字下げした行はフェンスではない`` () =
    // 字下げされたコードブロックの中身で、フェンスを開かない。
    Assert.Equal<string list>([ "見出し" ], topHeadings "    ```\n## 見出し\n")

[<Fact>]
let ``正規形の文書は木へ読んで戻しても変わらない`` () =
    let markdown = "前書き\n\n## 節\n\n本文\n\n### 小見出し\n\n中身"
    Assert.Equal(markdown, toMarkdown (parseSections markdown))

[<Fact>]
let ``toMarkdownは見出し行の余分な空白を揃え末尾の改行は保たない`` () =
    Assert.Equal("## 節\n\n本文", toMarkdown (parseSections "##   節  \n\n本文\n"))

[<Fact>]
let ``filterDescendantsは落とした節を子孫ごと消す`` () =
    let markdown = "## 残す\n\n本文\n\n## 落とす\n\n### 子\n\n子の中身\n\n## これも残す\n\n本文"

    let filtered =
        parseSections markdown
        |> filterDescendants (fun section -> section.Heading <> Some "落とす")
        |> toMarkdown

    Assert.Equal("## 残す\n\n本文\n\n## これも残す\n\n本文", filtered)

[<Fact>]
let ``filterDescendantsは深い階層の節も落とす`` () =
    let markdown = "## 親\n\n### 落とす\n\n中身\n\n### 残す\n\n中身"

    let filtered =
        parseSections markdown
        |> filterDescendants (fun section -> section.Heading <> Some "落とす")
        |> toMarkdown

    Assert.Equal("## 親\n\n### 残す\n\n中身", filtered)
