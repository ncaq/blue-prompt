/// webページのHTML取得。
module BluePrompt.Page

open System
open Microsoft.Playwright
open System.Threading.Tasks

/// URLへ遷移し、loadイベント後のDOM全体をHTML文字列で返す。
/// browserを引数に取ることで1つのブラウザで複数ページを取得できる。
let fetchHtml (browser: IBrowser) (url: Uri) : Task<string> =
    task {
        use! context = browser.NewContextAsync()
        let! page = context.NewPageAsync()
        let! _response = page.GotoAsync url.AbsoluteUri
        return! page.ContentAsync()
    }
