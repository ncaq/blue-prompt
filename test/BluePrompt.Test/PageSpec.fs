module BluePrompt.Test.PageSpec

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Xunit

/// 渡したHTMLだけを返すローカルHTTPサーバを立てて、そのURLをactionへ渡す。
/// 外部サイトの構造に依存せずにコンテンツ抽出を検証するための足場。
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

        // ブラウザはfaviconなども取りに来るため、全リクエストへ同じHTMLを返し続ける。
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
[<Trait("Category", "Browser")>]
let ``example.comのHTMLを取得できる`` () : Task =
    task {
        let! html =
            BluePrompt.Browser.withBrowser (fun browser ->
                BluePrompt.Page.fetchHtml browser (Uri "https://example.com/"))

        Assert.Contains("<title>Example Domain</title>", html)
    }

/// コンテンツ抽出の検証用HTML。
/// 除去対象のヘッダ類、外すべきリンク、結合セルとセル内改行を持つテーブルを1つに詰めている。
let private fixtureHtml =
    """<html><body>
<header id="header">site header</header>
<nav id="menu">sidebar</nav>
<main id="content">
<h1>Fixture</h1>
<p><a href="#section">anchor text</a></p>
<table>
<thead><tr><th>name</th><th>value</th></tr></thead>
<tbody>
<tr><td rowspan="2">merged</td><td>one<br>two</td></tr>
<tr><td><br>second</td></tr>
</tbody>
</table>
</main>
<div id="note">note text</div>
<footer id="footer">site footer</footer>
</body></html>"""

let private fixtureQuery: BluePrompt.Page.ContentQuery =
    { ContentSelectors = [ "#content"; "#note" ]
      RemoveSelectors = [ "#header"; "#menu"; "#footer" ]
      UnwrapLinks = true
      FlattenTables = true }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``fetchContentHtmlは指定した要素だけを抜き出し除去対象を含めない`` () : Task =
    task {
        let! html =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml fixtureHtml (fun url ->
                    BluePrompt.Page.fetchContentHtml browser url fixtureQuery))

        Assert.Contains("Fixture", html)
        Assert.Contains("note text", html)
        Assert.DoesNotContain("site header", html)
        Assert.DoesNotContain("sidebar", html)
        Assert.DoesNotContain("site footer", html)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``fetchContentHtmlはリンクを外しテーブルを平坦化する`` () : Task =
    task {
        let! html =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml fixtureHtml (fun url ->
                    BluePrompt.Page.fetchContentHtml browser url fixtureQuery))

        // リンクはタグだけ外れてテキストが残る。
        Assert.DoesNotContain("<a ", html)
        Assert.Contains("anchor text", html)
        // rowspanは各行へ複製展開され、セル内のbrは区切り文字になる。
        Assert.DoesNotContain("rowspan", html)
        Assert.Equal(2, Text.RegularExpressions.Regex.Matches(html, "merged").Count)
        Assert.Contains("one / two", html)
        // セル先頭のbrは区切り文字にせず取り除かれる。
        Assert.DoesNotContain("/ second", html)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``平坦化したテーブルはpandocでパイプテーブルへ変換できる`` () : Task =
    task {
        let! html =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml fixtureHtml (fun url ->
                    BluePrompt.Page.fetchContentHtml browser url fixtureQuery))

        let! markdown = BluePrompt.Pandoc.toMarkdown html
        // 結合セルやセル内改行が残っているとpandocはテーブルを[TABLE]へ潰してしまう。
        Assert.DoesNotContain("[TABLE]", markdown)
        // pandocはセル幅を空白で揃えるため、パディングに依存しない形で検証する。
        Assert.Matches(@"\| name\s+\| value\s+\|", markdown)
        Assert.Matches(@"\| merged\s+\| second\s+\|", markdown)
    }
