module BluePrompt.Test.ScrapeSpec

open System.Threading.Tasks
open Falco.Markup
open Xunit
open BluePrompt.Test.HtmlFixture
open BluePrompt.Test.MockServer

[<Fact>]
let ``ローカルサーバのHTMLをMarkdown化できる`` () : Task =
    task {
        // 外部サイト依存テストはnixのchecksから除外されるため、
        // 取得とpandoc変換の合成が繋がっていることをオフラインで固定する。
        use server = serveHtml (renderDocument [ Text.h1 "Local Page"; Text.p "paragraph" ])
        let! markdown = BluePrompt.Scrape.fetchMarkdown (baseUri server)
        Assert.Contains("# Local Page", markdown)
        Assert.Contains("paragraph", markdown)
    }
