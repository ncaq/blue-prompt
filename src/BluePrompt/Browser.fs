/// Playwrightブラウザのライフサイクル管理。
module BluePrompt.Browser

open Microsoft.Playwright
open System.Threading.Tasks

/// PlaywrightランタイムとChromiumを起動してactionへ渡し、完了後に必ず破棄する。
let withBrowser (action: IBrowser -> Task<'T>) : Task<'T> =
    task {
        use! playwright = Playwright.CreateAsync()
        use! browser = playwright.Chromium.LaunchAsync()
        return! action browser
    }
