module BluePrompt.Test.ScrapeSpec

open System
open System.Threading.Tasks
open Falco.Markup
open Xunit
open BluePrompt.Test.HtmlFixture
open BluePrompt.Test.LocalServer

[<Fact>]
let ``ローカルサーバのHTMLをMarkdown化できる`` () : Task =
    task {
        // 外部サイト依存テストはnixのchecksから除外されるため、
        // 取得とpandoc変換の合成が繋がっていることをオフラインで固定する。
        let html = renderDocument [ Text.h1 "Local Page"; Text.p "paragraph" ]
        let! markdown = withServedHtml html (fun url -> BluePrompt.Scrape.fetchMarkdown url)
        Assert.Contains("# Local Page", markdown)
        Assert.Contains("paragraph", markdown)
    }

[<Fact>]
[<Trait("Category", "Network")>]
let ``example.comのMarkdownに見出しとリンクがMarkdown記法で含まれる`` () : Task =
    task {
        let! markdown = BluePrompt.Scrape.fetchMarkdown (Uri "https://example.com/")
        Assert.Contains("# Example Domain", markdown)
        Assert.Contains("](https://iana.org/domains/example)", markdown)
    }
