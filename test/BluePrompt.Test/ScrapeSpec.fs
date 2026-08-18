module BluePrompt.Test.ScrapeSpec

open System
open System.Threading.Tasks
open Xunit

[<Fact>]
[<Trait("Category", "Browser")>]
let ``example.comのMarkdownにタイトルが見出しとして含まれる`` () : Task =
    task {
        let! markdown = BluePrompt.Scrape.fetchMarkdown (Uri "https://example.com/")
        Assert.Contains("# Example Domain", markdown)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``example.comのMarkdownにリンクがMarkdown記法で含まれる`` () : Task =
    task {
        let! markdown = BluePrompt.Scrape.fetchMarkdown (Uri "https://example.com/")
        Assert.Contains("](https://iana.org/domains/example)", markdown)
    }
