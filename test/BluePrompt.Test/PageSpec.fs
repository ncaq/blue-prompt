module BluePrompt.Test.PageSpec

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Xunit
open BluePrompt.Test.LocalServer

[<Fact>]
let ``ローカルサーバのHTMLを取得できる`` () : Task =
    task {
        let html =
            "<html><head><title>Served Page</title></head><body>body text</body></html>"

        let! fetched = withServedHtml html (fun url -> BluePrompt.Page.fetchHtml url)
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
        do!
            withServer
                (fun _ ->
                    { htmlResponse "<html><body>not found</body></html>" with
                        Status = "404 Not Found" })
                (fun url ->
                    task {
                        let! error =
                            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                                BluePrompt.Page.fetchHtml url :> Task)

                        match error :> exn with
                        | BluePrompt.Page.FetchError(failedUrl, status) ->
                            Assert.Equal(url, failedUrl)
                            Assert.Equal(404, status)
                        | unexpected -> raise unexpected
                    })
    }

[<Fact>]
let ``接続できない場合はFetchErrorになる`` () : Task =
    task {
        // AngleSharpのローダーは接続拒否やDNS解決失敗でも例外を投げず、
        // status=200の空ドキュメントを返すため、
        // 検知しないと空のHTMLが正常な取得として静かに返ってしまう。
        // 空きポートを確保してすぐ閉じることで、確実に接続拒否になるURLを作る。
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()

        let! error =
            Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                BluePrompt.Page.fetchHtml (Uri $"http://127.0.0.1:%d{port}/") :> Task)

        // レスポンス自体を得られなかった取得失敗はstatus=0として現れる。
        match error :> exn with
        | BluePrompt.Page.FetchError(_, status) -> Assert.Equal(0, status)
        | unexpected -> raise unexpected
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

        let response =
            { Status = "200 OK"
              ContentType = "text/html; charset=euc-jp"
              Body = (Encoding.GetEncoding "euc-jp").GetBytes "<html><body>日本語本文</body></html>"
              ExtraHeaders = [] }

        let! fetched = withServer (fun _ -> response) (fun url -> BluePrompt.Page.fetchHtml url)

        Assert.Contains("日本語本文", fetched)
    }

[<Fact>]
let ``別スキームへのリダイレクトは追跡されずFetchErrorになる`` () : Task =
    task {
        // file:等へのリダイレクトを実際に弾いているのはLoaderOptionsのフィルタではなく、
        // DefaultHttpRequesterがhttpとhttpsしか扱わないこと。
        // requester構成の変更でこの担保が崩れた時に検知できるように固定する。
        let respond (_: string) : Response =
            { htmlResponse "" with
                Status = "302 Found"
                ExtraHeaders = [ "Location: file:///etc/passwd" ] }

        do!
            withServer respond (fun url ->
                Assert.ThrowsAsync<BluePrompt.Page.FetchError>(fun () ->
                    BluePrompt.Page.fetchHtml url :> Task))
            :> Task
    }

[<Fact>]
let ``リダイレクトを追跡して最終ページを取得する`` () : Task =
    task {
        let respond (path: string) : Response =
            if path = "/moved" then
                htmlResponse "<html><body>redirected body</body></html>"
            else
                { htmlResponse "" with
                    Status = "302 Found"
                    ExtraHeaders = [ "Location: /moved" ] }

        let! fetched = withServer respond (fun url -> BluePrompt.Page.fetchHtml url)

        Assert.Contains("redirected body", fetched)
    }

/// 取得と抽出の結合の検証用HTML。
/// 抽出処理そのものの網羅的な検証はExtractSpecで行う。
let private fixtureHtml =
    """<html><body>
<header id="header">site header</header>
<main id="content">
<h1>Fixture</h1>
<p><a href="#section">anchor text</a></p>
</main>
</body></html>"""

let private fixtureQuery: BluePrompt.Extract.ContentQuery =
    { ContentSelectors = [ "#content" ]
      RemoveSelectors = [ "#header" ]
      UnwrapLinks = true
      ReplaceImagesWithAlt = false
      FlattenTables = true }

[<Fact>]
let ``fetchContentHtmlは取得したDOMへ抽出を適用する`` () : Task =
    task {
        let! html =
            withServedHtml fixtureHtml (fun url ->
                BluePrompt.Page.fetchContentHtml url fixtureQuery)

        Assert.Contains("Fixture", html)
        Assert.DoesNotContain("site header", html)
        // リンクはタグだけ外れてテキストが残る。
        Assert.DoesNotContain("<a ", html)
        Assert.Contains("anchor text", html)
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

        do!
            withServedHtml fixtureHtml (fun url ->
                task {
                    let! error =
                        Assert.ThrowsAsync<BluePrompt.Page.ContentNotFound>(fun () ->
                            BluePrompt.Page.fetchContentHtml url query :> Task)

                    match error :> exn with
                    | BluePrompt.Page.ContentNotFound(failedUrl, failedSelectors) ->
                        Assert.Equal(url, failedUrl)
                        Assert.Equal<string list>(selectors, failedSelectors)
                    | unexpected -> raise unexpected
                })
    }
