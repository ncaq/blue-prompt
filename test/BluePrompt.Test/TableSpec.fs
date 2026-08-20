module BluePrompt.Test.TableSpec

open AngleSharp.Html.Parser
open Xunit

[<Fact>]
let ``格子の予算は文書全体で共有され使い切った後のテーブルは展開されない`` () =
    // 総エントリ数の上限が1テーブル単位でしか効かないと、
    // 上限直下の表を大量に並べた文書全体では総量が上限×テーブル数まで膨らむ。
    // 予算が文書全体で持ち回られることを、小さい予算を直接渡して固定する。
    // 各テーブルの見積りは6エントリ(rowspan=3のセルで3と通常セル3つ)で、
    // 予算10では1つ目に展開したテーブルで残り4になり、もう1つは展開できない。
    let html =
        """<html><body>
<table><tbody>
<tr><td rowspan="3">first</td><td>a</td></tr>
<tr><td>b</td></tr>
<tr><td>c</td></tr>
</tbody></table>
<table><tbody>
<tr><td rowspan="3">second</td><td>a</td></tr>
<tr><td>b</td></tr>
<tr><td>c</td></tr>
</tbody></table>
</body></html>"""

    use document = HtmlParser().ParseDocument html
    BluePrompt.Table.flattenWithBudget 10L document

    // テーブルは内側優先の逆順で走査されるため、文書で後のテーブルが先に予算を使う。
    let unexpanded = document.QuerySelectorAll "td[rowspan]"
    Assert.Equal(1, unexpanded.Length)
    Assert.Contains("first", unexpanded[0].TextContent)
