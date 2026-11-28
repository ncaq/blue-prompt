module BluePrompt.Test.PageSpec

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open AngleSharp
open AngleSharp.Dom
open AngleSharp.Html.Dom
open Falco.Markup
open WireMock.RequestBuilders
open WireMock.ResponseBuilders
open WireMock.Server
open Xunit
open BluePrompt.Test.HtmlFixture
open BluePrompt.Test.MockServer

/// 指定したステータスでHTMLを返す応答。
let private statusResponse (status: int) (text: string) : IResponseBuilder =
    (htmlResponse (renderDocument [ Text.raw text ])).WithStatusCode(status)

/// 取得したDOM全体をHTMLの文字列にして返す。読み込みキャッシュは使わない。
/// 取得の挙動を、返ってきた中身の文字で確かめるための足場。
let private fetchHtmlWith (delays: TimeSpan list) (url: Uri) : Task<string> =
    BluePrompt.Page.fetchWith
        { RetryDelays = delays
          CacheDirectory = None }
        url
        (fun document -> document.ToHtml())

/// fetchHtmlWithを既定のリトライ間隔で呼ぶ。
let private fetchHtml (url: Uri) : Task<string> =
    fetchHtmlWith (BluePrompt.Page.defaultSettings ()).RetryDelays url

[<Fact>]
let ``ローカルサーバのHTMLを取得できる`` () : Task =
    task {
        let html =
            renderNode (
                Elem.html
                    []
                    [ Elem.head [] [ Elem.title [] [ Text.raw "Served Page" ] ]
                      Elem.body [] [ Text.raw "body text" ] ]
            )

        use server = serveHtml html
        let! fetched = fetchHtml (baseUri server)
        Assert.Contains("<title>Served Page</title>", fetched)
        Assert.Contains("body text", fetched)
    }

[<Fact>]
let ``成功以外のHTTPステータスはFetchErrorになる`` () : Task =
    task {
        // ローダー差し替えで最も壊れやすいステータス検査を固定する。
        use server = serve (statusResponse 404 "not found")
        let url = baseUri server

        let! error = Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () -> fetchHtml url :> Task)

        match error :> exn with
        | BluePrompt.Page.FetchError(failedUrl, status) ->
            Assert.Equal(url, failedUrl)
            Assert.Equal(404, status)
        | unexpected -> raise unexpected
    }

[<Fact>]
let ``本文が空のエラー応答は本来のステータスで報告される`` () : Task =
    task {
        // 空ドキュメント検知が先に働くと、ボディ無しの404が接続失敗と同じstatus=0へ潰れて、
        // 原因調査時に「サーバへ繋がらなかった」と誤読させる。
        use server = serve (Response.Create().WithStatusCode 404)
        let url = baseUri server

        let! error = Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () -> fetchHtml url :> Task)

        match error :> exn with
        | BluePrompt.Page.FetchError(failedUrl, status) ->
            Assert.Equal(url, failedUrl)
            Assert.Equal(404, status)
        | unexpected -> raise unexpected
    }

[<Fact>]
let ``接続できない場合はFetchErrorになる`` () : Task =
    task {
        // AngleSharpのローダーは接続拒否やDNS解決失敗でも例外を投げず、
        // status=200の空ドキュメントを返すため、
        // 検知しないと空のHTMLが正常な取得として静かに返ってしまう。
        // 空きポートを確保してすぐ閉じることで、確実に接続拒否になるURLを作る。
        // localhostがIPv4とIPv6のどちらへ解決されても拒否されるように、両方を受けるソケットで確保する。
        let listener = TcpListener.Create 0
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()

        // status=0はリトライの対象なので、既定の間隔で待たされないように空のリトライ間隔で呼ぶ。
        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                fetchHtmlWith [] (Uri $"http://localhost:%d{port}/") :> Task)

        // レスポンス自体を得られなかった取得失敗はstatus=0として現れる。
        match error :> exn with
        | BluePrompt.Page.FetchError(_, status) -> Assert.Equal(0, status)
        | unexpected -> raise unexpected
    }

[<Fact>]
let ``一時的な失敗はリトライして回復する`` () : Task =
    task {
        // サーバの一時的な不調を模して、最初の2回だけ500を返し3回目で成功させる。
        use server = WireMockServer.Start()

        server
            .Given(anyRequest ())
            .InScenario("一時的な不調")
            .WillSetStateTo("回復", 2)
            .RespondWith(statusResponse 500 "server error")
        |> ignore

        server
            .Given(anyRequest ())
            .InScenario("一時的な不調")
            .WhenStateIs("回復")
            .RespondWith(htmlResponse (renderDocument [ Text.raw "recovered body" ]))
        |> ignore

        // テストを待たせないため間隔は0にして、回数の消化だけを検証する。
        let! fetched = fetchHtmlWith [ TimeSpan.Zero; TimeSpan.Zero ] (baseUri server)

        Assert.Contains("recovered body", fetched)
        Assert.Equal(3, Seq.length server.LogEntries)
    }

[<Fact>]
let ``リトライが尽きたら失敗がそのまま届く`` () : Task =
    task {
        use server = serve (statusResponse 503 "unavailable")

        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                fetchHtmlWith [ TimeSpan.Zero ] (baseUri server) :> Task)

        match error :> exn with
        | BluePrompt.Page.FetchError(_, status) -> Assert.Equal(503, status)
        | unexpected -> raise unexpected

        // 最初の1回とリトライの1回で、リクエストは2回になる。
        Assert.Equal(2, Seq.length server.LogEntries)
    }

[<Fact>]
let ``一時的でない失敗はリトライしない`` () : Task =
    task {
        // 404はやり直しても結果が変わらないため、リトライ間隔が残っていても1回で諦める。
        use server = serve (statusResponse 404 "not found")

        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                fetchHtmlWith [ TimeSpan.Zero ] (baseUri server) :> Task)

        match error :> exn with
        | BluePrompt.Page.FetchError(_, status) -> Assert.Equal(404, status)
        | unexpected -> raise unexpected

        Assert.Equal(1, Seq.length server.LogEntries)
    }

[<Fact>]
let ``http以外のスキームはArgumentExceptionになる`` () : Task =
    task {
        // ブラウザ経由でなくなった今、このガードがローカルファイル読み出しを防ぐ唯一の砦。
        do!
            Assert.ThrowsAsync<ArgumentException>(fun () ->
                fetchHtml (Uri "file:///etc/passwd") :> Task)
            :> Task
    }

[<Fact>]
let ``Content-Typeのcharsetに従って文字コードが判定される`` () : Task =
    task {
        // wikiruはUTF-8だが、文字コードの判定をローダーに任せるという前提を固定する。
        // EUC-JPは.NETの組み込みではないため、テスト側でプロバイダを登録する。
        Encoding.RegisterProvider CodePagesEncodingProvider.Instance

        use server =
            serve (
                Response
                    .Create()
                    .WithHeader("Content-Type", "text/html; charset=euc-jp")
                    .WithBody(
                        (Encoding.GetEncoding "euc-jp")
                            .GetBytes(renderDocument [ Text.raw "日本語本文" ])
                    )
            )

        let! fetched = fetchHtml (baseUri server)

        Assert.Contains("日本語本文", fetched)
    }

[<Fact>]
let ``ページ内のリソースは追加取得されない`` () : Task =
    task {
        // IsResourceLoadingEnabled = falseはAngleSharpの既定値の変化への防御なので、
        // 設定が効かなくなった時に検知できるようにテストで固定する。
        use server =
            serveHtml (
                renderDocument [ Elem.img [ Attr.src "/image.png" ]; Text.p "resource test" ]
            )

        let! fetched = fetchHtml (baseUri server)

        Assert.Contains("resource test", fetched)
        Assert.DoesNotContain("/image.png", requestedPaths server)
    }

[<Fact>]
let ``別スキームへのリダイレクトは追跡されずFetchErrorになる`` () : Task =
    task {
        // file:等へのリダイレクトを実際に弾いているのはLoaderOptionsのフィルタではなく、
        // DefaultHttpRequesterがhttpとhttpsしか扱わないこと。
        // requester構成の変更でこの担保が崩れた時に検知できるように固定する。
        // この失敗はstatus=0でリトライの対象になるため、空のリトライ間隔で待たずに検証する。
        use server =
            serve (
                Response.Create().WithStatusCode(302).WithHeader("Location", "file:///etc/passwd")
            )

        do!
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                fetchHtmlWith [] (baseUri server) :> Task)
            :> Task
    }

[<Fact>]
let ``リダイレクトを追跡して最終ページを取得する`` () : Task =
    task {
        use server = WireMockServer.Start()

        server
            .Given(Request.Create().WithPath("/"))
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "/moved"))
        |> ignore

        server
            .Given(Request.Create().WithPath("/moved"))
            .RespondWith(htmlResponse (renderDocument [ Text.raw "redirected body" ]))
        |> ignore

        let! fetched = fetchHtml (baseUri server)

        Assert.Contains("redirected body", fetched)
    }

[<Fact>]
let ``読み込みキャッシュがあれば2回目は取得しに行かず同じDOMになる`` () : Task =
    task {
        // 変換の作りを直す間に同じページを何度も取りに行かないためのキャッシュ。
        // キャッシュから作ったDOMでも、相対URLが取得元を基準に解決されて文字も化けないことを固定する。
        let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())

        let settings: BluePrompt.Page.Settings =
            { RetryDelays = []
              CacheDirectory = Some directory }

        use server =
            serveHtml (renderDocument [ Elem.a [ Attr.href "./?other" ] [ Text.raw "日本語のリンク" ] ])

        let url = baseUri server

        let read (document: IDocument) : string * string =
            let anchor = document.QuerySelector "a" :?> IHtmlAnchorElement
            anchor.TextContent, anchor.Href

        try
            let! first = BluePrompt.Page.fetchWith settings url read
            let! second = BluePrompt.Page.fetchWith settings url read

            Assert.Equal<string * string>(("日本語のリンク", Uri(url, "./?other").AbsoluteUri), first)
            Assert.Equal<string * string>(first, second)
            Assert.Equal(1, Seq.length server.LogEntries)
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
    }

[<Fact>]
let ``読み込みキャッシュを使っても取得の失敗は保存されない`` () : Task =
    task {
        // 失敗した応答を保存すると、以後はキャッシュから空のページを読み続けてしまう。
        let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())

        let settings: BluePrompt.Page.Settings =
            { RetryDelays = []
              CacheDirectory = Some directory }

        use server = serve (statusResponse 404 "not found")

        try
            do!
                Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                    BluePrompt.Page.fetchWith settings (baseUri server) ignore :> Task)
                :> Task

            Assert.True(
                not (Directory.Exists directory)
                || Seq.isEmpty (Directory.EnumerateFiles directory)
            )
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
    }
