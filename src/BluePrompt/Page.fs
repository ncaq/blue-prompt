/// webページのHTML取得。
module BluePrompt.Page

open System
open Microsoft.Playwright
open System.Threading.Tasks

/// HTTPステータスが成功以外だった時のURLとステータスコード。
exception FetchError of url: Uri * status: int

/// URLへ遷移してloadイベントを待ち、開いたページをactionへ渡す。
/// HTTPステータスが成功以外の場合はFetchErrorを送出する。
/// file:等によるローカル読み出しを防ぐため、スキームはhttpとhttpsに限定する。
let private withPage (browser: IBrowser) (url: Uri) (action: IPage -> Task<'T>) : Task<'T> =
    task {
        if url.Scheme <> Uri.UriSchemeHttp && url.Scheme <> Uri.UriSchemeHttps then
            raise (ArgumentException($"http/https以外のスキームは扱えません: %O{url}", nameof url))

        use! context = browser.NewContextAsync()
        let! page = context.NewPageAsync()
        let! response = page.GotoAsync url.AbsoluteUri

        // about:blankへの遷移などではresponseがnullになるため、その場合は検査しない。
        match response with
        | null -> ()
        | response when not response.Ok -> raise (FetchError(url = url, status = response.Status))
        | _ -> ()

        return! action page
    }

/// URLへ遷移し、loadイベント後のDOM全体をHTML文字列で返す。
/// browserを引数に取ることで1つのブラウザで複数ページを取得できる。
/// エラー条件はwithPageと同じ。
let fetchHtml (browser: IBrowser) (url: Uri) : Task<string> =
    withPage browser url (fun page -> page.ContentAsync())

/// URLへ遷移し、queryに従って本文だけをHTML文字列として抜き出す。
/// ブラウザはページのJavaScript実行後のDOMを取得するためだけに使い、
/// 除去や変形と抽出はExtract.contentHtmlのF#実装で行う。
/// 抽出の挙動と失敗条件はExtract.contentHtmlと同じで、取得のエラー条件はwithPageと同じ。
let fetchContentHtml (browser: IBrowser) (url: Uri) (query: Extract.ContentQuery) : Task<string> =
    task {
        let! html = fetchHtml browser url
        return Extract.contentHtml url query html
    }
