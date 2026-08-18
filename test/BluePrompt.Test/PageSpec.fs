module BluePrompt.Test.PageSpec

open System
open System.Threading.Tasks
open Xunit

[<Fact>]
[<Trait("Category", "Browser")>]
let ``example.comのHTMLを取得できる`` () : Task =
    task {
        let! html =
            BluePrompt.Browser.withBrowser (fun browser ->
                BluePrompt.Page.fetchHtml browser (Uri "https://example.com/"))

        Assert.Contains("<title>Example Domain</title>", html)
    }
