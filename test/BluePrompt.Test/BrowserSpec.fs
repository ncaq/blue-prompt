module BluePrompt.Test.BrowserSpec

open Microsoft.Playwright
open System
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

[<Fact>]
[<Trait("Category", "Browser")>]
let ``withBrowserは完了後にブラウザを破棄する`` () : Task =
    task {
        let! browser = BluePrompt.Browser.withBrowser Task.FromResult
        Assert.False browser.IsConnected
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``actionが例外を投げても例外は伝播しブラウザは破棄される`` () : Task =
    task {
        // actionへ渡されたブラウザを持ち出して、例外後に破棄されたことを確認する。
        let escaped = TaskCompletionSource<IBrowser>()

        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                BluePrompt.Browser.withBrowser (fun browser ->
                    escaped.SetResult browser
                    Task.FromException<int>(InvalidOperationException "boom"))
                :> Task)

        Assert.Equal("boom", error.Message)
        let! browser = escaped.Task
        Assert.False browser.IsConnected
    }
