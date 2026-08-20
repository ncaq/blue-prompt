/// webページのHTML取得。
module BluePrompt.Page

open System
open System.Threading.Tasks
open AngleSharp
open AngleSharp.Io

/// HTTPステータスが成功以外だった時のURLとステータスコード。
/// 接続拒否やDNS解決失敗などでレスポンス自体を得られなかった取得失敗は、
/// withDocumentが空ドキュメントの検知で写像したstatus=0として現れる。
exception FetchError of url: Uri * status: int

/// コンテンツ抽出でどのセレクタも要素に一致しなかった時の取得元URLとセレクタ一覧。
/// Extract.ContentNotFoundへ取得の文脈である取得元URLを付け足した例外。
exception ContentNotFound of url: Uri * selectors: string list

/// 取得に使う共有の設定。
/// スキーム制限は二段で成り立っている。
/// 初回リクエストはwithDocumentの明示的なガードが検査し、
/// ローダーが自動追跡するリダイレクトの先は、
/// DefaultHttpRequesterがhttpとhttpsしか扱わないことで担保される。
/// LoaderOptions.Filterは初回リクエストにしか適用されず、
/// 明示的なガードと重複して全リクエストを検査しているような誤解を招くだけなので使わない。
/// FileRequesterなどのrequesterを追加するとリダイレクト先の担保が崩れる点に注意。
/// 制限しているのはスキームだけで宛先ホストは制限していないため、
/// リダイレクトで内部ホストへ誘導される余地は残るが、
/// 取得先をユーザ自身が指定するローカルCLIの脅威モデルでは許容する。
/// img/iframe/cssなどのリソースの追加取得をしないことは、
/// AngleSharpの既定値が将来変わっても影響を受けないように明示する。
/// リクエスタごと共有することで、呼び出しごとの生成コストと接続の作り直しを避ける。
let private configuration =
    Configuration.Default.WithDefaultLoader(LoaderOptions(IsResourceLoadingEnabled = false))

/// URLを取得し、パース済みDOMをactionへ渡してその結果を返す。
/// AngleSharpのローダーで取得するためブラウザは不要で、JavaScriptは実行しない。
/// 文字コードの判定とリダイレクトの追跡はローダーがHTML仕様に沿って行う。
/// HTTPステータスが成功以外の場合はFetchErrorを送出する。
/// file:等によるローカル読み出しを防ぐため、スキームはhttpとhttpsに限定する。
/// ブラウジングコンテキストは開いているドキュメントを持つ状態機械なので、
/// 共有せず呼び出しごとに作って破棄する。
let private withDocument (url: Uri) (action: AngleSharp.Dom.IDocument -> 'T) : Task<'T> =
    task {
        if url.Scheme <> Uri.UriSchemeHttp && url.Scheme <> Uri.UriSchemeHttps then
            raise (ArgumentException($"http/https以外のスキームは扱えません: %O{url}", nameof url))

        use context = BrowsingContext.New configuration
        use! document = context.OpenAsync url.AbsoluteUri

        // AngleSharpのローダーは接続拒否やDNS解決失敗でも例外を投げず、
        // status=200の空ドキュメントを返す。
        // 素通りすると空のHTMLが正常な取得として流れてしまうため、
        // ソースが空のドキュメントを取得失敗としてstatus=0のFetchErrorへ写像する。
        // 本文が完全に空の応答も同じ姿になるが、抽出できるものが無い点は取得失敗と変わらない。
        if document.Source.Text.Length = 0 then
            raise (FetchError(url = url, status = 0))

        let status = int document.StatusCode

        if status < 200 || 300 <= status then
            raise (FetchError(url = url, status = status))

        return action document
    }

/// URLを取得し、パース済みDOM全体をHTML文字列で返す。
/// エラー条件はwithDocumentと同じ。
let fetchHtml (url: Uri) : Task<string> =
    withDocument url (fun document -> document.ToHtml())

/// URLを取得し、queryに従って本文だけをHTML文字列として抜き出す。
/// ローダーが構築したDOMをそのままExtractへ渡すため、直列化と再パースは行わない。
/// 抽出の挙動はExtract.contentHtmlOfDocumentと同じで、取得のエラー条件はwithDocumentと同じ。
/// 抽出が全滅した場合はExtract.ContentNotFoundを取得元URL付きのContentNotFoundへ包み直す。
let fetchContentHtml (url: Uri) (query: Extract.ContentQuery) : Task<string> =
    task {
        try
            return! withDocument url (Extract.contentHtmlOfDocument query)
        with Extract.ContentNotFound selectors ->
            return raise (ContentNotFound(url = url, selectors = selectors))
    }
