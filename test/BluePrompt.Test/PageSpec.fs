module BluePrompt.Test.PageSpec

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Xunit

/// 渡したHTMLだけを返すローカルHTTPサーバを立てて、そのURLをactionへ渡す。
/// 外部サイトの構造やネットワークに依存せずにページ取得を検証するための足場。
let private withServedHtml (html: string) (action: Uri -> Task<'T>) : Task<'T> =
    task {
        // ポート0でOSに空きポートを割り当てさせて他のテストとの衝突を避ける。
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        let body = Encoding.UTF8.GetBytes html

        let header =
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: %d{body.Length}\r\n"
            + "Connection: close\r\n\r\n"

        // 全リクエストへ同じHTMLを返し続ける。
        // listener.Stop()でAcceptが例外になりループごと終了する。
        let serving =
            task {
                while true do
                    use! client = listener.AcceptTcpClientAsync()
                    let stream = client.GetStream()
                    let buffer = (Array.zeroCreate 8192: byte array).AsMemory()
                    let! _ = stream.ReadAsync buffer
                    do! stream.WriteAsync((Encoding.ASCII.GetBytes header).AsMemory())
                    do! stream.WriteAsync(body.AsMemory())
            }

        try
            return! action (Uri $"http://127.0.0.1:%d{port}/")
        finally
            listener.Stop()
            ignore serving
    }

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
