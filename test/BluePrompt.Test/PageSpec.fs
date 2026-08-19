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
      ReplaceImagesWithAlt = false
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
        // rowspanは各行へ複製展開され、セル内のbrは読点で繋がれる。
        Assert.DoesNotContain("rowspan", html)
        Assert.Equal(2, Text.RegularExpressions.Regex.Matches(html, "merged").Count)
        Assert.Contains("one、two", html)
        // セル先頭のbrは繋ぐ相手がいないので取り除かれる。
        Assert.DoesNotContain("、second", html)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``ReplaceImagesWithAltは画像をaltの文字列へ置き換え空altやファイル名だけの画像は取り除く`` () : Task =
    task {
        let html =
            """<html><body><main id="content">
<p><img src="a.png" alt="素材名"><img src="b.png" alt=""><img src="c.png">
<img src="d.png" alt="アイコン_0.PNG"></p>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ]
                ReplaceImagesWithAlt = true }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        Assert.Contains("素材名", extracted)
        Assert.DoesNotContain("<img", extracted)
        Assert.DoesNotContain("アイコン_0.PNG", extracted)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``セル内のブロック要素は区切り文字を挟んで平坦化される`` () : Task =
    task {
        // セル内にdivやpが残っているとpandocがパイプテーブルで表現できず、
        // テーブル全体を[TABLE]へ潰してしまう。
        let html =
            """<html><body><main id="content">
<table>
<tbody>
<tr><td><div>one</div><div>two</div><div></div></td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        Assert.Contains("one、two", extracted)
        Assert.DoesNotContain("<div", extracted)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``セル内の改行は前後の句読点に合わせて詰めるか読点で繋ぐ`` () : Task =
    task {
        let html =
            """<html><body><main id="content">
<table>
<tbody>
<tr><td>文がある。<br>次の文</td><td>3146<br>/47611</td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        // 句点の後ろは詰め、区切り記号で始まる行の前も詰める。
        Assert.Contains("文がある。次の文", extracted)
        Assert.Contains("3146/47611", extracted)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``横に結合されたセルの複製は空になる`` () : Task =
    task {
        // 横方向の複製は同じ行を読めば分かる繰り返しでしかなく、
        // 長いテキストを複製すると行が際限なく伸びる。
        let html =
            """<html><body><main id="content">
<table>
<tbody>
<tr><th colspan="3">横に長い見出し</th></tr>
<tr><td>a</td><td>b</td><td>c</td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        Assert.DoesNotContain("colspan", extracted)
        Assert.Equal(1, Text.RegularExpressions.Regex.Matches(extracted, "横に長い見出し").Count)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``表を横断する1セルだけの行は段落として表の外へ出る`` () : Task =
    task {
        // 小見出しや自由記述の行が列を持つ行と同じ表に混ざっていると、
        // 長い記述に合わせた列幅の整形で他の行が際限なく伸びる。
        let html =
            """<html><body><main id="content">
<table>
<tbody>
<tr><th colspan="2">小見出し</th></tr>
<tr><td colspan="2">自由記述の長い文章。</td></tr>
<tr><td>a</td><td>b</td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        // 見出しセルは強調付きの段落、通常セルはただの段落になり、表には残らない。
        Assert.Contains("<strong>小見出し</strong>", extracted)
        Assert.Contains("<p>自由記述の長い文章。</p>", extracted)
        Assert.DoesNotContain("<td>小見出し", extracted)
        Assert.Contains("<td>a</td>", extracted)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``見出しの下が全て空の列は取り除かれる`` () : Task =
    task {
        // 画像だけの列は画像の除去で見出しを残して空になり、ノイズの列として残ってしまう。
        let html =
            """<html><body><main id="content">
<table>
<tbody>
<tr><th>画像</th><th>名前</th></tr>
<tr><td><img src="a.png"></td><td>ユウカ</td></tr>
<tr><td><img src="b.png"></td><td>ノア</td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ]
                RemoveSelectors = [ "img" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        Assert.DoesNotContain("画像", extracted)
        Assert.Contains("<td>ユウカ</td>", extracted)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``キーと値のペアを横に並べた行は1行1ペアの2列になる`` () : Task =
    task {
        // th,td,th,td,...と続く行はキーと値のペアを横に詰めたレイアウトで、
        // 表として読むと列の意味が揃わず混乱する。
        let html =
            """<html><body><main id="content">
<table>
<tbody>
<tr><th>HP</th><td>3146</td><th>攻撃力</th><td>118</td><th></th><td></td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        Assert.Contains("<tr><th>HP</th><td>3146</td></tr>", extracted)
        Assert.Contains("<tr><th>攻撃力</th><td>118</td></tr>", extracted)
        // 中身が両方空の埋め草ペアは行にならない。
        Assert.Equal(2, Text.RegularExpressions.Regex.Matches(extracted, "<tr>").Count)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``全セルが空の行は取り除かれる`` () : Task =
    task {
        // 画像だけの行は画像の除去で全セルが空になり、ノイズの行として残ってしまう。
        let html =
            """<html><body><main id="content">
<table>
<tbody>
<tr><td><img src="a.png"></td><td><img src="b.png"></td></tr>
<tr><td>name</td><td>value</td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ]
                RemoveSelectors = [ "img" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        Assert.Contains("name", extracted)
        Assert.Equal(1, Text.RegularExpressions.Regex.Matches(extracted, "<tr>").Count)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``レイアウト用テーブルのセル内にある本文のdivは平坦化されない`` () : Task =
    task {
        // ページ全体をテーブルで組むレイアウトでは、本文のコンテナ自体がセル内のdivになる。
        // これを外すとContentSelectorsが空振りして本文を見失う。
        let html =
            """<html><body>
<table><tbody><tr><td>
<div id="content"><h1>Fixture</h1><p>first</p><p>second</p></div>
</td></tr></tbody></table>
</body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        Assert.Contains("id=\"content\"", extracted)
        Assert.Contains("Fixture", extracted)
        // レイアウト用テーブルのセル内では、本文の段落も外されず区切り文字も入らない。
        Assert.Contains("<p>second</p>", extracted)
        Assert.DoesNotContain(" / ", extracted)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``過大なrowspanは実際の行数で切り詰められる`` () : Task =
    task {
        // rowspan/colspanは外部HTML由来の未検証値で、HTML仕様上は65534と1000まで指定できる。
        // そのまま格子を組むと数千万要素へ膨らんで処理がハングするため、切り詰めを検証する。
        let html =
            """<html><body><main id="content">
<table>
<thead><tr><th>name</th><th>value</th></tr></thead>
<tbody>
<tr><td rowspan="65534">big</td><td>one</td></tr>
<tr><td>two</td></tr>
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        let! extracted =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml html (fun url -> BluePrompt.Page.fetchContentHtml browser url query))

        // 実際の行数(2行)分だけ展開され、指定値までは複製されない。
        Assert.Equal(2, Text.RegularExpressions.Regex.Matches(extracted, "big").Count)
        Assert.DoesNotContain("rowspan", extracted)
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``全ContentSelectorsが一致しない場合はContentNotFoundになる`` () : Task =
    task {
        // サイト側のid変更などで抽出が全滅した時に、
        // 空文字列が正常な結果として返ると空のナレッジで既存ファイルを上書きしてしまう。
        // 全セレクタ0件一致は例外として検知できることを検証する。
        let query =
            { fixtureQuery with
                ContentSelectors = [ "#missing"; "#also-missing" ] }

        do!
            Assert.ThrowsAsync<BluePrompt.Page.ContentNotFound>(fun () ->
                BluePrompt.Browser.withBrowser (fun browser ->
                    withServedHtml fixtureHtml (fun url ->
                        BluePrompt.Page.fetchContentHtml browser url query))
                :> Task)
            :> Task
    }

[<Fact>]
[<Trait("Category", "Browser")>]
let ``一部のContentSelectorsが一致しなくても残りは抽出される`` () : Task =
    task {
        // wikiruの#note(脚注)のように任意の要素があるため、個別のセレクタの0件一致は許容する。
        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content"; "#missing" ] }

        let! html =
            BluePrompt.Browser.withBrowser (fun browser ->
                withServedHtml fixtureHtml (fun url ->
                    BluePrompt.Page.fetchContentHtml browser url query))

        Assert.Contains("Fixture", html)
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
