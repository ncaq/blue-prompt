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
let ``knowledgeHeaderは出典URLを含む`` () =
    Assert.Contains(
        "https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8",
        BluePrompt.Wikiru.knowledgeHeader "キャラ呼称表"
    )
