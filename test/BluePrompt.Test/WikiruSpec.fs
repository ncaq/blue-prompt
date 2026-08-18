module BluePrompt.Test.WikiruSpec

open Xunit

[<Fact>]
let ``pageUriはページ名をパーセントエンコードしたURLを組み立てる`` () =
    Assert.Equal(
        "https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8",
        (BluePrompt.Wikiru.pageUri "キャラ呼称表").AbsoluteUri
    )

[<Fact>]
let ``cleanupMarkdownはコメント欄の見出しの残骸を取り除く`` () =
    let markdown = "# 本文\n\n内容\n\n## コメントフォーム\n\n付記\n"
    Assert.Equal("# 本文\n\n内容\n\n付記\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``cleanupMarkdownは連続する空行を1つへ潰す`` () =
    let markdown = "a\n\n\n\nb\n"
    Assert.Equal("a\n\nb\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``cleanupMarkdownは最初の見出しより前のナビゲーションを切り落とす`` () =
    let markdown = "一覧 \\| 索引\n\n最終更新日時:2026-08-15\n\n## 本文\n\n内容\n"
    Assert.Equal("## 本文\n\n内容\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``cleanupMarkdownは脚注をGFMの文法へ変換する`` () =
    let markdown = "# 本文\n\n| ノノミ | 十六夜ノノミさん\\*1 |\n\n\\*1 初対面時  \n"

    Assert.Equal(
        "# 本文\n\n| ノノミ | 十六夜ノノミさん[^1] |\n\n[^1]: 初対面時\n",
        BluePrompt.Wikiru.cleanupMarkdown markdown
    )

[<Fact>]
let ``脚注定義の変換は後続の段落を巻き込まない`` () =
    // 定義行にテキストが無い場合、正規表現が改行を跨いでマッチすると、
    // 無関係な次の段落が脚注定義へ吸い込まれてしまう。
    let markdown = "# h\n\n\\*1\n\n次の段落\n"
    Assert.Equal("# h\n\n[^1]\n\n次の段落\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``2桁の脚注も変換される`` () =
    let markdown = "# h\n\n本文\\*10\n\n\\*10 条件\n"
    Assert.Equal("# h\n\n本文[^10]\n\n[^10]: 条件\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``連続する脚注定義はそれぞれ変換される`` () =
    let markdown = "# h\n\n\\*1 初対面時  \n\\*2 変装中  \n"

    Assert.Equal("# h\n\n[^1]: 初対面時\n[^2]: 変装中\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``エスケープされていないアスタリスクは変換されない`` () =
    // pandocがエスケープしなかった*1は脚注由来ではないため素通りする。
    let markdown = "# h\n\n*1 これは脚注ではない\n"
    Assert.Equal("# h\n\n*1 これは脚注ではない\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``knowledgeHeaderは出典URLを含む`` () =
    Assert.Contains(
        "https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8",
        BluePrompt.Wikiru.knowledgeHeader "キャラ呼称表"
    )
