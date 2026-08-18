module BluePrompt.Test.BrowserSpec

open System.Threading.Tasks
open Xunit

[<Fact>]
[<Trait("Category", "Browser")>]
let ``withBrowserはブラウザを起動しactionの結果を返す`` () : Task =
    task {
        let! isConnected =
            BluePrompt.Browser.withBrowser (fun browser -> Task.FromResult browser.IsConnected)

        Assert.True isConnected
    }
