/// webページのHTML取得。
module BluePrompt.Page

open System
open Microsoft.Playwright
open System.Threading.Tasks

/// HTTPステータスが成功以外だった時のURLとステータスコード。
exception FetchError of url: Uri * status: int

/// URLへ遷移し、loadイベント後のDOM全体をHTML文字列で返す。
/// browserを引数に取ることで1つのブラウザで複数ページを取得できる。
/// HTTPステータスが成功以外の場合はFetchErrorを送出する。
let fetchHtml (browser: IBrowser) (url: Uri) : Task<string> =
    task {
        use! context = browser.NewContextAsync()
        let! page = context.NewPageAsync()
        let! response = page.GotoAsync url.AbsoluteUri

        // about:blankへの遷移などではresponseがnullになるため、その場合は検査しない。
        match response with
        | null -> ()
        | response when not response.Ok -> raise (FetchError(url = url, status = response.Status))
        | _ -> ()

        return! page.ContentAsync()
    }
