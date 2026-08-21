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

    Assert.Equal<string list list>(
        [ [ "学校A"; "部活X" ]; [ "学校A"; "部活Y" ]; [ "学校B" ] ],
        fragments |> List.map _.Headings
    )

[<Fact>]
let ``断片には祖先の見出しと文書全体の前書きが前置される`` () =
    let fragments = splitBySize 60 sample

    // 部活Xの断片だけを読んでも、どの学校の何なのかと出典が分かる。
    Assert.Equal("出典: どこかのページ\n\n## 学校A\n### 部活X\n\nXの説明。\n", fragments.Head.Text)

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
