/// webページのHTML取得。
module BluePrompt.Page

open System
open System.Threading.Tasks
open AngleSharp

/// HTTPステータスが成功以外だった時のURLとステータスコード。
exception FetchError of url: Uri * status: int

/// URLを取得し、パース済みDOM全体をHTML文字列で返す。
/// AngleSharpのローダーで取得するためブラウザは不要で、JavaScriptは実行しない。
/// 文字コードの判定とリダイレクトの追跡はローダーがHTML仕様に沿って行う。
/// HTTPステータスが成功以外の場合はFetchErrorを送出する。
/// file:等によるローカル読み出しを防ぐため、スキームはhttpとhttpsに限定する。
let fetchHtml (url: Uri) : Task<string> =
    task {
        if url.Scheme <> Uri.UriSchemeHttp && url.Scheme <> Uri.UriSchemeHttps then
            raise (ArgumentException($"http/https以外のスキームは扱えません: %O{url}", nameof url))

        use context = BrowsingContext.New(Configuration.Default.WithDefaultLoader())
        use! document = context.OpenAsync url.AbsoluteUri
        let status = int document.StatusCode

        if status < 200 || 300 <= status then
            raise (FetchError(url = url, status = status))

        return document.ToHtml()
    }

/// URLを取得し、queryに従って本文だけをHTML文字列として抜き出す。
/// 抽出の挙動と失敗条件はExtract.contentHtmlと同じで、取得のエラー条件はfetchHtmlと同じ。
let fetchContentHtml (url: Uri) (query: Extract.ContentQuery) : Task<string> =
    task {
        let! html = fetchHtml url
        return Extract.contentHtml url query html
    }
