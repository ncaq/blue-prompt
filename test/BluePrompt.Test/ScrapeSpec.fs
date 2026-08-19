module BluePrompt.Test.ScrapeSpec

open System
open System.Threading.Tasks
open Xunit

[<Fact>]
[<Trait("Category", "Network")>]
let ``example.comのMarkdownに見出しとリンクがMarkdown記法で含まれる`` () : Task =
    task {
        let! markdown = BluePrompt.Scrape.fetchMarkdown (Uri "https://example.com/")
        Assert.Contains("# Example Domain", markdown)
        Assert.Contains("](https://iana.org/domains/example)", markdown)
    }
