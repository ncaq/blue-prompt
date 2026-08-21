module BluePrompt.Test.ExtractSpec

open System
open System.Threading.Tasks
open Xunit

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

let private fixtureQuery: BluePrompt.Extract.ContentQuery =
    { ContentSelectors = [ "#content"; "#note" ]
      RemoveSelectors = [ "#header"; "#menu"; "#footer" ]
      UnwrapLinks = true
      ReplaceImagesWithAlt = false
      FlattenTables = true }

[<Fact>]
let ``contentHtmlは指定した要素だけを抜き出し除去対象を含めない`` () =
    let html = BluePrompt.Extract.contentHtml fixtureQuery fixtureHtml

    Assert.Contains("Fixture", html)
    Assert.Contains("note text", html)
    Assert.DoesNotContain("site header", html)
    Assert.DoesNotContain("sidebar", html)
    Assert.DoesNotContain("site footer", html)

[<Fact>]
let ``contentHtmlはリンクを外しテーブルを平坦化する`` () =
    let html = BluePrompt.Extract.contentHtml fixtureQuery fixtureHtml

    // リンクはタグだけ外れてテキストが残る。
    Assert.DoesNotContain("<a ", html)
    Assert.Contains("anchor text", html)
    // rowspanは各行へ複製展開され、セル内のbrは読点で繋がれる。
    Assert.DoesNotContain("rowspan", html)
    Assert.Equal(2, Text.RegularExpressions.Regex.Matches(html, "merged").Count)
    Assert.Contains("one、two", html)
    // セル先頭のbrは繋ぐ相手がいないので取り除かれる。
    Assert.DoesNotContain("、second", html)

[<Fact>]
let ``ReplaceImagesWithAltは画像をaltの文字列へ置き換え空altやファイル名だけの画像は取り除く`` () =
    let html =
        """<html><body><main id="content">
<p><img src="a.png" alt="素材名"><img src="b.png" alt=""><img src="c.png">
<img src="d.png" alt="アイコン_0.PNG"></p>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ]
            ReplaceImagesWithAlt = true }

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("素材名", extracted)
    Assert.DoesNotContain("<img", extracted)
    Assert.DoesNotContain("アイコン_0.PNG", extracted)

[<Fact>]
let ``セル内のブロック要素は区切り文字を挟んで平坦化される`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("one、two", extracted)
    Assert.DoesNotContain("<div", extracted)

[<Fact>]
let ``セル内の改行は前後の句読点に合わせて詰めるか読点で繋ぐ`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    // 句点の後ろは詰め、区切り記号で始まる行の前も詰める。
    Assert.Contains("文がある。次の文", extracted)
    Assert.Contains("3146/47611", extracted)

[<Fact>]
let ``見出しセル内の改行は読点を挟まず詰める`` () =
    // 見出しセルは文ではなくラベルで、改行は表示幅の都合の折り返しでしかない。
    // 読点を挟むと「各バージョン、一覧」のようにラベルが分断されて読める。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><th>各バージョン<br>一覧</th><td>値あり</td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("各バージョン一覧", extracted)
    Assert.DoesNotContain("各バージョン、一覧", extracted)

[<Fact>]
let ``同じリンク先をまたぐ改行は読点を挟まず詰める`` () =
    // wikiruでは長いリンクラベルを同じリンク先の複数のaへ分けてbrで折り返すことがある。
    // 読点を挟むと「ミレニアムサイエンス、スクール2年生」のように固有名詞が分断される。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><th>学園</th>
<td><a href="./?school#m">ミレニアムサイエンス</a><br><a href="./?school#m">スクール2年生</a></td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("ミレニアムサイエンススクール2年生", extracted)

[<Fact>]
let ``異なるリンク先をまたぐ改行は読点で繋ぐ`` () =
    // 別々のリンク先が並ぶのは入手手段のような列挙なので、読点の区切りを維持する。
    // 同じリンク先の折り返しを詰める規則が効き過ぎないことの固定。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><th>入手手段</th><td><a href="./?a">通常募集</a><br><a href="./?b">アーカイブ募集</a></td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("通常募集、アーカイブ募集", extracted)

[<Fact>]
let ``横に結合されたセルの複製は空になる`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.DoesNotContain("colspan", extracted)
    Assert.Equal(1, Text.RegularExpressions.Regex.Matches(extracted, "横に長い見出し").Count)

[<Fact>]
let ``表を横断する1セルだけの行は段落として表の外へ出る`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    // 見出しセルは強調付きの段落、通常セルはただの段落になり、表には残らない。
    Assert.Contains("<strong>小見出し</strong>", extracted)
    Assert.Contains("<p>自由記述の長い文章。</p>", extracted)
    Assert.DoesNotContain("<td>小見出し", extracted)
    Assert.Contains("<td>a</td>", extracted)

[<Fact>]
let ``表の途中の区切り行は前後の行を別々の表へ分ける`` () =
    // 区切り行は表の先頭だけでなく途中にも現れる。
    // その場合は段落を挟んで前後が別々の表として続く。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><td>a1</td><td>a2</td></tr>
<tr><th colspan="2">途中の小見出し</th></tr>
<tr><td>b1</td><td>b2</td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    // 前半の表、段落、後半の表の順序が保たれる。
    Assert.True(extracted.IndexOf "a1" < extracted.IndexOf "途中の小見出し")
    Assert.True(extracted.IndexOf "途中の小見出し" < extracted.IndexOf "b1")
    Assert.Contains("<strong>途中の小見出し</strong>", extracted)
    // 前半の行と後半の行は同じ表に残らない。
    Assert.Equal(2, Text.RegularExpressions.Regex.Matches(extracted, "<table>").Count)

[<Fact>]
let ``見出しの下が全て空の列は取り除かれる`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.DoesNotContain("画像", extracted)
    Assert.Contains("<td>ユウカ</td>", extracted)

[<Fact>]
let ``キーと値のペアを横に並べた行は1行1ペアの2列になる`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("<tr><th>HP</th><td>3146</td></tr>", extracted)
    Assert.Contains("<tr><th>攻撃力</th><td>118</td></tr>", extracted)
    // 中身が両方空の埋め草ペアは行にならない。
    Assert.Equal(2, Text.RegularExpressions.Regex.Matches(extracted, "<tr>").Count)

[<Fact>]
let ``見出し行の無い表では先頭行にだけ値がある列も残る`` () =
    // 見出しだけの列の削除はth見出し行を持つ表のための処理で、
    // データ行から始まる表の先頭行を見出し扱いすると、
    // 先頭行にだけ値がある列が事実データごと静かに消えてしまう。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><td>先頭だけの値</td><td>a</td></tr>
<tr><td></td><td>b</td></tr>
<tr><td></td><td>c</td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("先頭だけの値", extracted)

[<Fact>]
let ``全セルが空の行は取り除かれる`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("name", extracted)
    Assert.Equal(1, Text.RegularExpressions.Regex.Matches(extracted, "<tr>").Count)

[<Fact>]
let ``レイアウト用テーブルのセル内にある本文のdivは平坦化されない`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("id=\"content\"", extracted)
    Assert.Contains("Fixture", extracted)
    // レイアウト用テーブルのセル内では、本文の段落も外されず区切り文字も入らない。
    Assert.Contains("<p>second</p>", extracted)
    Assert.DoesNotContain(" / ", extracted)

[<Fact>]
let ``過大なrowspanは実際の行数で切り詰められる`` () =
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

    let extracted = BluePrompt.Extract.contentHtml query html

    // 実際の行数(2行)分だけ展開され、指定値までは複製されない。
    Assert.Equal(2, Text.RegularExpressions.Regex.Matches(extracted, "big").Count)
    Assert.DoesNotContain("rowspan", extracted)

[<Fact>]
let ``過大なcolspanは上限の列数で切り詰められる`` () =
    // colspanの切り詰めを直接検証する。
    // 総量上限のテストは展開されない経路しか通らず、
    // rowspanのテストは縦方向しか見ないため、この定数を変えても検知できなかった。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><td colspan="1000">wide</td><td>y</td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    // 1000列ではなく上限の100列(元セル1つと空の複製99個)とyで101セルになる。
    Assert.Equal(101, Text.RegularExpressions.Regex.Matches(extracted, "<td").Count)
    Assert.DoesNotContain("colspan", extracted)

[<Fact>]
let ``2行の表では見出しに中身のある空列も残る`` () =
    // 2行の表では空セルがデータの欠けを意味しうるため、
    // 見出しに中身のある列の削除は3行以上の表に限られる。その境界を固定する。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><th>画像</th><th>名前</th></tr>
<tr><td></td><td>ユウカ</td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Contains("画像", extracted)
    Assert.Contains("ユウカ", extracted)

[<Fact>]
let ``rowspan0のセルは残りの行の末尾まで展開される`` () =
    // HTML仕様のrowspan="0"は「セクションの最後まで」を意味し、DOMのrowSpanは0を返す。
    // 0のまま展開するとループが1度も回らずセルが格子から消え、事実データが静かに落ちる。
    let html =
        """<html><body><main id="content">
<table>
<tbody>
<tr><td rowspan="0">スパン</td><td>one</td></tr>
<tr><td>two</td></tr>
</tbody>
</table>
</main></body></html>"""

    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content" ] }

    let extracted = BluePrompt.Extract.contentHtml query html

    Assert.Equal(2, Text.RegularExpressions.Regex.Matches(extracted, "スパン").Count)
    Assert.DoesNotContain("rowspan", extracted)

[<Fact>]
let ``格子が総量の上限を超える表は展開されずそのまま残る`` () : Task =
    task {
        // rowspan/colspanの切り詰めは1セルあたりの上限でしかなく、
        // 総量は行数×1行のセル数×rowSpan×colSpanで二次的に膨らむ。
        // 悪意ある値や編集ミスでメモリ枯渇やハングに至らないように、
        // 見積りが上限を超える表は展開を諦めてそのまま残ることを固定する。
        let makeRow (index: int) : string =
            $"""<tr><td rowspan="65534" colspan="1000">c%d{index}</td></tr>"""

        let rows = String.concat "\n" (List.init 200 makeRow)

        let html =
            $"""<html><body><main id="content">
<table>
<tbody>
%s{rows}
</tbody>
</table>
</main></body></html>"""

        let query =
            { fixtureQuery with
                ContentSelectors = [ "#content" ] }

        // 防御が壊れた時の退行は事実上の無限ループで、
        // そのまま待つとCIごと停止するため、
        // 上限時間を挟んでテストの失敗として観測できるようにする。
        let timeout = TimeSpan.FromSeconds 60.
        let work = Task.Run(fun () -> BluePrompt.Extract.contentHtml query html)
        let! finished = Task.WhenAny(work, Task.Delay timeout)
        let completedInTime = obj.ReferenceEquals(finished, work)

        Assert.True(completedInTime, $"総量上限を超える表の処理が%.0f{timeout.TotalSeconds}秒以内に完走しませんでした")

        let extracted = work.Result

        // 展開されない証拠としてrowspan属性が残る。データ自体は落ちない。
        Assert.Contains("rowspan", extracted)
        Assert.Contains("c199", extracted)
    }

[<Fact>]
let ``全ContentSelectorsが一致しない場合はContentNotFoundになる`` () =
    // サイト側のid変更などで抽出が全滅した時に、
    // 空文字列が正常な結果として返ると空のナレッジで既存ファイルを上書きしてしまう。
    // 全セレクタ0件一致は例外として検知できることを検証する。
    let selectors = [ "#missing"; "#also-missing" ]

    let query =
        { fixtureQuery with
            ContentSelectors = selectors }

    let error =
        Assert.Throws<BluePrompt.Extract.ContentNotFound>(fun () ->
            BluePrompt.Extract.contentHtml query fixtureHtml |> ignore)

    // この例外は何をどのセレクタで探して失敗したかを人へ伝えるのが役目なので、
    // 型だけでなくペイロードまで検証してフィールドの取り違えを検知する。
    match error :> exn with
    | BluePrompt.Extract.ContentNotFound failedSelectors ->
        Assert.Equal<string list>(selectors, failedSelectors)
    | unexpected -> raise unexpected

[<Fact>]
let ``一部のContentSelectorsが一致しなくても残りは抽出される`` () =
    // wikiruの#note(脚注)のように任意の要素があるため、個別のセレクタの0件一致は許容する。
    let query =
        { fixtureQuery with
            ContentSelectors = [ "#content"; "#missing" ] }

    let html = BluePrompt.Extract.contentHtml query fixtureHtml

    Assert.Contains("Fixture", html)

[<Fact>]
let ``平坦化したテーブルはpandocでパイプテーブルへ変換できる`` () : Task =
    task {
        let html = BluePrompt.Extract.contentHtml fixtureQuery fixtureHtml
        let! markdown = BluePrompt.Pandoc.toMarkdown html
        // 結合セルやセル内改行が残っているとpandocはテーブルを[TABLE]へ潰してしまう。
        Assert.DoesNotContain("[TABLE]", markdown)
        // pandocはセル幅を空白で揃えるため、パディングに依存しない形で検証する。
        Assert.Matches(@"\| name\s+\| value\s+\|", markdown)
        Assert.Matches(@"\| merged\s+\| second\s+\|", markdown)
    }
