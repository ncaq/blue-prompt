module BluePrompt.Test.ScrapeSpec

open System
open System.Threading.Tasks
open Xunit

[<Fact>]
[<Trait("Category", "Browser")>]
let ``1つのブラウザを再利用して複数回Markdown化できる`` () : Task =
    task {
        let url = Uri "https://example.com/"

        let! first, second =
            BluePrompt.Browser.withBrowser (fun browser ->
                task {
                    let! first = BluePrompt.Scrape.toMarkdown browser url
                    let! second = BluePrompt.Scrape.toMarkdown browser url
                    return first, second
                })

        Assert.Contains("# Example Domain", first)
        Assert.Contains("# Example Domain", second)
    }

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
