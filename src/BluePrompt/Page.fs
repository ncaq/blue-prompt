/// webページのHTML取得。
module BluePrompt.Page

open System
open System.Threading.Tasks
open AngleSharp
open AngleSharp.Dom
open AngleSharp.Io

/// HTTPステータスが成功以外だった時のURLとステータスコード。
/// 接続拒否やDNS解決失敗などでレスポンス自体を得られなかった取得失敗は、
/// withDocumentが空ドキュメントの検知で写像したstatus=0として現れる。
exception FetchError of url: Uri * status: int

/// コンテンツ抽出でどのセレクタも要素に一致しなかった時の取得元URLとセレクタ一覧。
/// Extract.ContentNotFoundへ取得の文脈である取得元URLを付け足した例外。
exception ContentNotFound of url: Uri * selectors: string list

/// リトライで解決しうる一時的な失敗かどうか。
/// status=0はレスポンス自体を得られなかった接続失敗で、
/// 一瞬のネットワーク断やDNSの不調なら時間を置くと成功しうる。
/// 408と429と5xxもサーバ側の一時的な状態なので同様。
/// それ以外の4xxはリクエスト自体の問題なので、やり直しても結果は変わらない。
let private isTransientStatus (status: int) : bool =
    status = 0 || status = 408 || status = 429 || 500 <= status

/// fetchを実行し、一時的な取得失敗ならdelaysの先頭の時間だけ待ってやり直す。
/// delaysの長さがやり直し回数の上限で、尽きた後の失敗はそのまま呼び出し元へ届く。
/// 再帰関数をtaskの中へ置くとステートマシンを静的にコンパイルできなくなるため、モジュールの直下に置く。
let rec private retryTransient (delays: TimeSpan list) (fetch: unit -> Task<'T>) : Task<'T> =
    task {
        match delays with
        | [] -> return! fetch ()
        | delay :: rest ->
            try
                return! fetch ()
            with FetchError(_, status) when isTransientStatus status ->
                do! Task.Delay delay
                return! retryTransient rest fetch
    }

/// 一時的な取得失敗をやり直すまでの待ち時間の既定値。
/// 前から順に使う指数バックオフで、長さの3回が既定のやり直し回数の上限。
/// wikiruのたまの不調をやり過ごせて、
/// 本当の障害では待ち合わせの合計が数秒台で諦める長さにしている。
let defaultRetryDelays: TimeSpan list =
    [ TimeSpan.FromSeconds 1.0; TimeSpan.FromSeconds 2.0; TimeSpan.FromSeconds 4.0 ]

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

/// URLを1回だけ取得し、パース済みDOMをactionへ渡してその結果を返す。
/// AngleSharpのローダーで取得するためブラウザは不要で、JavaScriptは実行しない。
/// 文字コードの判定とリダイレクトの追跡はローダーがHTML仕様に沿って行う。
/// HTTPステータスが成功以外の場合はFetchErrorを送出する。
/// ブラウジングコンテキストは開いているドキュメントを持つ状態機械なので、
/// 共有せず呼び出しごとに作って破棄する。
let private openDocument (url: Uri) (action: IDocument -> 'T) : Task<'T> =
    task {
        use context = BrowsingContext.New configuration
        use! document = context.OpenAsync url.AbsoluteUri

        // ステータス検査を空ドキュメントの検知より先に行うことで、
        // ボディ無しの404のような本文が空のエラー応答を本来のステータスで報告する。
        let status = int document.StatusCode

        if status < 200 || 300 <= status then
            raise (FetchError(url = url, status = status))

        // AngleSharpのローダーは接続拒否やDNS解決失敗でも例外を投げず、
        // status=200の空ドキュメントを返すため、ステータス検査を素通りする。
        // 素通りすると空のHTMLが正常な取得として流れてしまうので、
        // ソースが空のドキュメントを取得失敗としてstatus=0のFetchErrorへ写像する。
        // 成功ステータスで本文が完全に空の応答も同じ姿になるが、
        // 抽出できるものが無い点は取得失敗と変わらない。
        // Source.Textはページ全体の文字列を毎回新規生成するため、O(1)のLengthで判定する。
        if document.Source.Length = 0 then
            raise (FetchError(url = url, status = 0))

        return action document
    }

/// URLを取得し、パース済みDOMをactionへ渡してその結果を返す。
/// 一時的な取得失敗はdelaysの間隔でリトライしてから諦める。
/// file:等によるローカル読み出しを防ぐため、スキームはhttpとhttpsに限定する。
/// スキームの検査はやり直しても結果が変わらないため、リトライの外で1回だけ行う。
let private withDocument (delays: TimeSpan list) (url: Uri) (action: IDocument -> 'T) : Task<'T> =
    task {
        if url.Scheme <> Uri.UriSchemeHttp && url.Scheme <> Uri.UriSchemeHttps then
            raise (ArgumentException($"http/https以外のスキームは扱えません: %O{url}", nameof url))

        return! retryTransient delays (fun () -> openDocument url action)
    }

/// URLを取得し、パース済みDOM全体をHTML文字列で返す。
/// 一時的な取得失敗はdelaysの間隔でリトライする。
/// HTTPステータスが成功以外の場合と取得自体の失敗(status=0)はFetchErrorを、
/// http/https以外のスキームにはArgumentExceptionを送出する。
let fetchHtmlWith (delays: TimeSpan list) (url: Uri) : Task<string> =
    withDocument delays url (fun document -> document.ToHtml())

/// fetchHtmlWithを既定のリトライ間隔で呼ぶ。
let fetchHtml (url: Uri) : Task<string> = fetchHtmlWith defaultRetryDelays url

/// URLを取得し、queryに従って本文だけをHTML文字列として抜き出す。
/// 一時的な取得失敗はdelaysの間隔でリトライする。
/// ローダーが構築したDOMをそのままExtractへ渡すため、直列化と再パースは行わない。
/// 抽出の挙動はExtract.contentHtmlOfDocumentと同じ。
/// HTTPステータスが成功以外の場合と取得自体の失敗(status=0)はFetchErrorを、
/// http/https以外のスキームにはArgumentExceptionを送出する。
/// 抽出が全滅した場合はExtract.ContentNotFoundを取得元URL付きのContentNotFoundへ包み直す。
let fetchContentHtmlWith
    (delays: TimeSpan list)
    (url: Uri)
    (query: Extract.ContentQuery)
    : Task<string> =
    task {
        try
            return! withDocument delays url (Extract.contentHtmlOfDocument query)
        with Extract.ContentNotFound selectors ->
            return raise (ContentNotFound(url = url, selectors = selectors))
    }

/// fetchContentHtmlWithを既定のリトライ間隔で呼ぶ。
let fetchContentHtml (url: Uri) (query: Extract.ContentQuery) : Task<string> =
    fetchContentHtmlWith defaultRetryDelays url query
