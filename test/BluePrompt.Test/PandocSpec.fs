module BluePrompt.Test.PandocSpec

open System.Threading.Tasks
open Xunit

[<Fact>]
let ``h1タグはATX見出しに変換される`` () : Task =
    task {
        let! markdown = BluePrompt.Pandoc.toMarkdown "<h1>Example Domain</h1>"
        Assert.Equal("# Example Domain", markdown.Trim())
    }

[<Fact>]
let ``長い段落は行折り返しされず1行になる`` () : Task =
    task {
        let sentence = String.replicate 30 "This paragraph is intentionally long. "
        let! markdown = BluePrompt.Pandoc.toMarkdown $"<p>%s{sentence}</p>"
        Assert.DoesNotContain("\n", markdown.Trim())
    }

[<Fact>]
let ``数MB規模のHTMLもハングせず変換できる`` () : Task =
    task {
        let html = String.replicate 100000 "<p>large input</p>"
        let! markdown = BluePrompt.Pandoc.toMarkdown html
        Assert.Contains("large input", markdown)
    }

[<Fact>]
let ``変換できないタグの生HTMLは残らない`` () : Task =
    task {
        let! markdown =
            BluePrompt.Pandoc.toMarkdown """<p><span style="color: red">text</span></p>"""

        Assert.DoesNotContain("<span", markdown)
        Assert.Contains("text", markdown)
    }

[<Fact>]
let ``tableはパイプテーブルに変換される`` () : Task =
    task {
        let html =
            "<table><tr><th>name</th><th>value</th></tr><tr><td>foo</td><td>1</td></tr></table>"

        let! markdown = BluePrompt.Pandoc.toMarkdown html
        // pandocはセル幅を空白で揃えるため、パディングに依存しない形で検証する。
        Assert.Contains("| name | value |", markdown)
        Assert.Matches(@"\| foo\s+\| 1\s+\|", markdown)
    }
