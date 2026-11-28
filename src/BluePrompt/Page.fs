/// webページのHTML取得。
/// AngleSharpのローダーで取得するためJavaScriptは実行しない。
/// wikiruはJavaScriptを実行しなくても最終的なMarkdownが変わらないことを確かめてある。
/// 描画にJavaScriptが要るサイトを扱う時は、この取得の層を差し替える必要がある。
module BluePrompt.Page

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open AngleSharp
open AngleSharp.Dom
open AngleSharp.Io
open Polly
open Polly.Retry

/// HTTPステータスが成功以外だった時のURLとステータスコード。
/// 接続拒否やDNS解決失敗などでレスポンス自体を得られなかった取得失敗は、
/// 空ドキュメントの検知で写像したstatus=0として現れる。
exception FetchError of url: Uri * status: int

/// リトライで解決しうる一時的な失敗かどうか。
/// status=0はレスポンス自体を得られなかった接続失敗で、
/// 一瞬のネットワーク断やDNSの不調なら時間を置くと成功しうる。
/// 408と429と5xxもサーバ側の一時的な状態なので同様。
/// それ以外の4xxはリクエスト自体の問題なので、やり直しても結果は変わらない。
let private isTransientStatus (status: int) : bool =
    status = 0 || status = 408 || status = 429 || 500 <= status

/// 一時的な取得失敗を、delaysを前から順に待ちながらやり直すパイプライン。
/// delaysの長さがやり直し回数の上限で、尽きた後の失敗はそのまま呼び出し元へ届く。
/// Pollyのやり直しは1回以上を求めるため、delaysが空ならやり直さないパイプラインにする。
let private retryPipeline (delays: TimeSpan list) : ResiliencePipeline =
    match Array.ofList delays with
    | [||] -> ResiliencePipeline.Empty
    | delays ->
        ResiliencePipelineBuilder()
            .AddRetry(
                RetryStrategyOptions(
                    ShouldHandle =
                        (fun arguments ->
                            match arguments.Outcome.Exception with
                            | FetchError(_, status) -> ValueTask<bool>(isTransientStatus status)
                            | _ -> ValueTask<bool> false),
                    MaxRetryAttempts = delays.Length,
                    DelayGenerator =
                        fun arguments ->
                            ValueTask<Nullable<TimeSpan>>(Nullable delays[arguments.AttemptNumber])
                )
            )
            .Build()

/// 一時的な取得失敗をやり直すまでの待ち時間の既定値。
/// 前から順に使う指数バックオフで、長さの3回が既定のやり直し回数の上限。
/// wikiruのたまの不調をやり過ごせて、
/// 本当の障害では待ち合わせの合計が数秒台で諦める長さにしている。
let defaultRetryDelays: TimeSpan list =
    [ TimeSpan.FromSeconds 1.0; TimeSpan.FromSeconds 2.0; TimeSpan.FromSeconds 4.0 ]

/// 取得のしかた。
type Settings =
    {
        /// 一時的な取得失敗をやり直すまでの待ち時間。
        /// 前から順に使い、長さがやり直し回数の上限になる。
        RetryDelays: TimeSpan list
        /// 開発用の読み込みキャッシュを置くディレクトリ。Noneなら使わない。
        CacheDirectory: string option
    }

/// 開発用の読み込みキャッシュを置くディレクトリを指す環境変数の名前。
///
/// 変換の作りを直している間は、同じページを何度も変換し直すことになる。
/// その度にwikiruへ取りに行くと相手に余計な負担を掛ける上に、
/// 取得の合間にwikiが編集されて、差分が変換の違いなのか内容の違いなのか見分けられなくなる。
/// キャッシュを指定すると、取得したHTMLの原文をそこへ置いて、以後はそこから読む。
/// 生成物をwikiの今の内容へ更新する時には指定しない。
let cacheDirectoryVariable = "BLUE_PROMPT_HTML_CACHE"

/// 既定の取得のしかた。
/// 読み込みキャッシュは環境変数が指している時だけ使う。
/// 環境変数を読むのはここだけにして、
/// テストがプロセス全体の環境を書き換えずにキャッシュの有無を選べるようにする。
let defaultSettings () : Settings =
    { RetryDelays = defaultRetryDelays
      CacheDirectory =
        match Environment.GetEnvironmentVariable cacheDirectoryVariable with
        | Str.NullOrEmpty -> None
        | Str.NonEmpty directory -> Some directory }

/// 取得に使う共有の設定。
/// スキーム制限は二段で成り立っている。
/// 初回リクエストはfetchWithの明示的なガードが検査し、
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

/// 取得した文書がエラー応答でも空でもないことを確かめる。
/// HTTPステータスが成功以外の場合と、応答を得られなかった場合にFetchErrorを送出する。
let private ensureFetched (url: Uri) (document: IDocument) : unit =
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

/// 読み込みキャッシュを使っていることを知らせ済みかどうか。0が未通知。
let mutable private cacheAnnounced = 0

/// 読み込みキャッシュを使っていることを、プロセスで1回だけ標準エラーへ知らせる。
/// キャッシュから作った生成物はwikiの今の内容ではないので、気付かないままコミットしないようにする。
let private announceCache (directory: string) : unit =
    if Interlocked.Exchange(&cacheAnnounced, 1) = 0 then
        eprintfn $"HTMLの読み込みキャッシュを使います: %s{directory}"

/// URLのHTMLをキャッシュする時のファイルのパス。
/// ページ名をパーセントエンコードしたURLはファイル名の長さの上限を超えうるため、
/// URLのハッシュを名前にする。
let private cachePath (directory: string) (url: Uri) : string =
    let hash =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes url.AbsoluteUri))

    Path.Combine(directory, hash + ".html")

/// 読み込みキャッシュからURLのHTMLの原文を得る。無ければ取得して保存する。
/// 保存するのはエラー応答でも空でもないことを確かめた後なので、失敗した取得は残らない。
let private cachedHtml (directory: string) (url: Uri) : Task<string> =
    task {
        let path = cachePath directory url

        if File.Exists path then
            return! File.ReadAllTextAsync path
        else
            use context = BrowsingContext.New configuration
            use! document = context.OpenAsync url.AbsoluteUri
            ensureFetched url document
            let html = document.Source.Text
            Directory.CreateDirectory directory |> ignore
            // 同じページを複数の対象が同時に取りに行くため、
            // 書きかけのファイルを他の読み手に見せないように、別名で書き終えてから置き換える。
            let temporary = Path.Combine(directory, Path.GetRandomFileName())
            do! File.WriteAllTextAsync(temporary, html)
            File.Move(temporary, path, true)
            return html
    }

/// URLを1回だけ取得し、パース済みDOMをactionへ渡してその結果を返す。
/// AngleSharpのローダーで取得するためブラウザは不要で、JavaScriptは実行しない。
/// 文字コードの判定とリダイレクトの追跡はローダーがHTML仕様に沿って行う。
/// HTTPステータスが成功以外の場合はFetchErrorを送出する。
/// ブラウジングコンテキストは開いているドキュメントを持つ状態機械なので、
/// 共有せず呼び出しごとに作って破棄する。
///
/// 読み込みキャッシュを使う時は、キャッシュに有っても無くても、
/// 保存した原文の文字列から元のURLを基準にしたDOMを作る。
/// 取得したDOMをそのまま使う経路を残すと、
/// 初回と2回目以降とで別の経路を通ることになり、同じ入力から同じ生成物が出ることを保てない。
let private openDocument
    (cacheDirectory: string option)
    (url: Uri)
    (action: IDocument -> 'T)
    : Task<'T> =
    task {
        match cacheDirectory with
        | None ->
            use context = BrowsingContext.New configuration
            use! document = context.OpenAsync url.AbsoluteUri
            ensureFetched url document
            return action document
        | Some directory ->
            announceCache directory
            let! html = cachedHtml directory url
            use context = BrowsingContext.New configuration

            // 文字列で渡した本文には応答ヘッダの文字コードが無いため、
            // ページのmetaに頼らずに済むように明示する。
            use! document =
                context.OpenAsync(fun (response: VirtualResponse) ->
                    response
                        .Content(html)
                        .Address(url)
                        .Header("Content-Type", "text/html; charset=utf-8")
                    |> ignore)

            return action document
    }

/// URLを取得し、パース済みDOMをactionへ渡してその結果を返す。
/// 一時的な取得失敗はsettingsの間隔でリトライしてから諦める。
/// DOMは取得のたびに作って破棄するので、actionの外へ持ち出さず、要る値だけを取り出して返す。
/// HTTPステータスが成功以外の場合と取得自体の失敗(status=0)はFetchErrorを、
/// http/https以外のスキームにはArgumentExceptionを送出する。
/// file:等によるローカル読み出しを防ぐため、スキームはhttpとhttpsに限定する。
/// スキームの検査はやり直しても結果が変わらないため、リトライの外で1回だけ行う。
let fetchWith (settings: Settings) (url: Uri) (action: IDocument -> 'T) : Task<'T> =
    task {
        if url.Scheme <> Uri.UriSchemeHttp && url.Scheme <> Uri.UriSchemeHttps then
            raise (ArgumentException($"http/https以外のスキームは扱えません: %O{url}", nameof url))

        return!
            (retryPipeline settings.RetryDelays)
                .ExecuteAsync(fun (_: CancellationToken) ->
                    ValueTask<'T>(openDocument settings.CacheDirectory url action))
                .AsTask()
    }

/// fetchWithを既定の取得のしかたで呼ぶ。
let fetch (url: Uri) (action: IDocument -> 'T) : Task<'T> =
    fetchWith (defaultSettings ()) url action
