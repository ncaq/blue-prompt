module BluePrompt.Test.PageSpec

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
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
        let! fetched = BluePrompt.Page.fetchHtml (baseUri server)
        Assert.Contains("<title>Served Page</title>", fetched)
        Assert.Contains("body text", fetched)
    }

[<Fact>]
[<Trait("Category", "Network")>]
let ``example.comのHTMLを取得できる`` () : Task =
    task {
        let! html = BluePrompt.Page.fetchHtml (Uri "https://example.com/")
        Assert.Contains("<title>Example Domain</title>", html)
    }

[<Fact>]
let ``成功以外のHTTPステータスはFetchErrorになる`` () : Task =
    task {
        // ローダー差し替えで最も壊れやすいステータス検査を固定する。
        use server = serve (statusResponse 404 "not found")
        let url = baseUri server

        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                BluePrompt.Page.fetchHtml url :> Task)

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

        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                BluePrompt.Page.fetchHtml url :> Task)

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
                BluePrompt.Page.fetchHtmlWith [] (Uri $"http://localhost:%d{port}/") :> Task)

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
        let! fetched =
            BluePrompt.Page.fetchHtmlWith [ TimeSpan.Zero; TimeSpan.Zero ] (baseUri server)

        Assert.Contains("recovered body", fetched)
        Assert.Equal(3, Seq.length server.LogEntries)
    }

[<Fact>]
let ``リトライが尽きたら失敗がそのまま届く`` () : Task =
    task {
        use server = serve (statusResponse 503 "unavailable")

        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                BluePrompt.Page.fetchHtmlWith [ TimeSpan.Zero ] (baseUri server) :> Task)

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
                BluePrompt.Page.fetchHtmlWith [ TimeSpan.Zero ] (baseUri server) :> Task)

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
                BluePrompt.Page.fetchHtml (Uri "file:///etc/passwd") :> Task)
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

        let! fetched = BluePrompt.Page.fetchHtml (baseUri server)

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

        let! fetched = BluePrompt.Page.fetchHtml (baseUri server)

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
                BluePrompt.Page.fetchHtmlWith [] (baseUri server) :> Task)
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

        let! fetched = BluePrompt.Page.fetchHtml (baseUri server)

        Assert.Contains("redirected body", fetched)
    }

/// 取得と抽出の結合の検証用HTML。
/// 抽出処理そのものの網羅的な検証はExtractSpecで行う。
let private fixtureHtml =
    renderDocument
        [ Elem.header [ Attr.id "header" ] [ Text.raw "site header" ]
          Elem.main
              [ Attr.id "content" ]
              [ Text.h1 "Fixture"
                Elem.p [] [ Elem.a [ Attr.href "#section" ] [ Text.raw "anchor text" ] ] ] ]

let private fixtureQuery: BluePrompt.Extract.ContentQuery =
    { ContentSelectors = [ "#content" ]
      RemoveSelectors = [ "#header" ]
      UnwrapInternalLinks = true
      ResolveImages = false
      FlattenTables = true
      TablesToLists = false }

[<Fact>]
let ``fetchContentHtmlは取得したDOMへ抽出を適用する`` () : Task =
    task {
        use server = serveHtml fixtureHtml
        let! html = BluePrompt.Page.fetchContentHtml (baseUri server) fixtureQuery

        Assert.Contains("Fixture", html)
        Assert.DoesNotContain("site header", html)
        // リンクはタグだけ外れてテキストが残る。
        Assert.DoesNotContain("<a ", html)
        Assert.Contains("anchor text", html)
    }

[<Fact>]
let ``fetchContentHtmlは取得元のURLを基準にリンクと画像を解決する`` () : Task =
    task {
        // 相対参照とサイト内の判定は、文字列からのパースでは決まらず取得元のURLで決まる。
        let html =
            renderDocument
                [ Elem.main
                      [ Attr.id "content" ]
                      [ Elem.p
                            []
                            [ Elem.a [ Attr.href "./?other" ] [ Text.raw "internal text" ]
                              Elem.a
                                  [ Attr.href "https://example.com/post" ]
                                  [ Text.raw "external text" ]
                              Elem.img
                                  [ Attr.class' "lazyload"
                                    Attr.create "data-src" "attach2/icon.png"
                                    Attr.src "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP"
                                    Attr.alt "素材名" ] ] ] ]

        let query =
            { fixtureQuery with
                ResolveImages = true }

        use server = serveHtml html
        let url = baseUri server
        let! extracted = BluePrompt.Page.fetchContentHtml url query

        // 取得元と同じホストへのリンクは外れる。
        Assert.DoesNotContain("other", extracted)
        Assert.Contains("internal text", extracted)

        Assert.Contains("""<a href="https://example.com/post">external text</a>""", extracted)
        // 画像はdata-srcを取得元のURLで解決し、srcとaltだけを持つ。
        Assert.Contains(
            $"""<img alt="素材名" src="%s{Uri(url, "attach2/icon.png").AbsoluteUri}">""",
            extracted
        )
    }

[<Fact>]
let ``抽出が全滅した場合は取得元URL付きのContentNotFoundになる`` () : Task =
    task {
        // Extract側の例外はセレクタしか持たないため、
        // Pageがどのページで全滅したのかを取得元URL付きの例外へ包み直すことを検証する。
        let selectors = [ "#missing" ]

        let query =
            { fixtureQuery with
                ContentSelectors = selectors }

        use server = serveHtml fixtureHtml
        let url = baseUri server

        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.ContentNotFound>(fun () ->
                BluePrompt.Page.fetchContentHtml url query :> Task)

        match error :> exn with
        | BluePrompt.Page.ContentNotFound(failedUrl, failedSelectors) ->
            Assert.Equal(url, failedUrl)
            Assert.Equal<string list>(selectors, failedSelectors)
        | unexpected -> raise unexpected
    }
