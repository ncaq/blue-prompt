module BluePrompt.Test.AppellationSpec

open Xunit
open BluePrompt.Appellation

/// 呼称表のテーブルHTMLを見出し行付きで組み立てる。
let private appellationTable (rows: string) : string =
    "<table><thead><tr><th>キャラクター</th><th>相手</th><th>呼称</th></tr></thead><tbody>"
    + rows
    + "</tbody></table>"

[<Fact>]
let ``見出しの階層が学校と部活とキャラクターへ対応付く`` () =
    let html =
        "<div id=\"body\">"
        + "<h2>アビドス高等学校 <a class=\"anchor_super\" href=\"#a\">†</a></h2>"
        + "<h3>対策委員会</h3>"
        + "<h4>ホシノ</h4>"
        + appellationTable "<tr><td>ホシノ</td><td>自分</td><td>私</td></tr>"
        + "</div>"

    Assert.Equal<Entry list>(
        [ { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Caller = "ホシノ"
            Callee = "自分"
            CalleeNote = None
            Name = "私"
            Note = None } ],
        parseHtml html
    )

[<Fact>]
let ``部活の見出しが無い学校ではClubはNoneになる`` () =
    // 連邦生徒会はh2直下にキャラクターのh4が並ぶ。
    let html =
        "<div id=\"body\"><h2>連邦生徒会</h2><h4>リン</h4>"
        + appellationTable "<tr><td>リン</td><td>自分</td><td>私</td></tr>"
        + "</div>"

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("連邦生徒会", entry.School)
    Assert.Equal(None, entry.Club)

[<Fact>]
let ``呼称は読点とbr要素のどちらでも区切られる`` () =
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>ホシノ</h4>"
        + appellationTable "<tr><td>ホシノ</td><td>自分</td><td>私、おじさん<br class=\"spacer\">わし</td></tr>"
        + "</div>"

    Assert.Equal<string list>([ "私"; "おじさん"; "わし" ], parseHtml html |> List.map _.Name)

[<Fact>]
let ``脚注は#noteの定義から呼称の注釈として解決される`` () =
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>ホシノ</h4>"
        + appellationTable (
            "<tr><td>ホシノ</td><td><a href=\"#Nonomi\">ノノミ</a></td>"
            + "<td>ノノミちゃん<br class=\"spacer\">十六夜ノノミさん"
            + "<a id=\"notetext_1\" href=\"#notefoot_1\" class=\"note_super\" title=\"初対面時\">*1</a>"
            + "</td></tr>"
        )
        + "</div>"
        + "<div id=\"note\"><a id=\"notefoot_1\" href=\"#notetext_1\" class=\"note_super\">*1</a>"
        + "<span class=\"small\">初対面のとき</span><br></div>"

    // 本文へ*1の文字が混入せず、注釈はtitle属性ではなく#noteの定義を正とする。
    Assert.Equal<(string * string option) list>(
        [ "ノノミちゃん", None; "十六夜ノノミさん", Some "初対面のとき" ],
        parseHtml html |> List.map (fun entry -> entry.Name, entry.Note)
    )

[<Fact>]
let ``脚注の定義を辿れない場合はtitle属性で代替する`` () =
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>ホシノ</h4>"
        + appellationTable (
            "<tr><td>ホシノ</td><td>ヒフミ</td>"
            + "<td>ファウストさん<a href=\"#notefoot_9\" class=\"note_super\" title=\"覆面時\">*9</a>"
            + "</td></tr>"
        )
        + "</div>"

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal(Some "覆面時", entry.Note)

[<Fact>]
let ``相手の名前に付いた脚注はCalleeNoteとして解決される`` () =
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>ノノミ</h4>"
        + appellationTable (
            "<tr><td>ノノミ</td>"
            + "<td>Mr.ニコライ<a href=\"#notefoot_10\" class=\"note_super\" title=\"モモフレンズ\">*10</a>"
            + "</td>"
            + "<td>ミスター・ニコライ</td></tr>"
        )
        + "</div>"

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("Mr.ニコライ", entry.Callee)
    Assert.Equal(Some "モモフレンズ", entry.CalleeNote)

[<Fact>]
let ``キャラクターの見出しより前にあるテーブルは読み飛ばされる`` () =
    // ページ冒頭の目次テーブルはどのキャラクターにも属さない。
    let html =
        "<div id=\"body\">"
        + "<table><tr><td>Table of Contents</td></tr></table>"
        + "<h2>学校</h2><h3>部活</h3><h4>ホシノ</h4>"
        + appellationTable "<tr><td>ホシノ</td><td>自分</td><td>私</td></tr>"
        + "</div>"

    Assert.Equal("私", (List.exactlyOne (parseHtml html)).Name)

[<Fact>]
let ``rowspanで結合されたキャラクター列の継続行も相手と呼称として読める`` () =
    // キャラクター列は先頭行だけに置かれ、2行目以降は相手と呼称の2セルになる。
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>ホシノ</h4>"
        + appellationTable (
            "<tr><td rowspan=\"2\">ホシノ</td><td>自分</td><td>私</td></tr>"
            + "<tr><td>シロコ</td><td>シロコちゃん</td></tr>"
        )
        + "</div>"

    Assert.Equal<(string * string) list>(
        [ "自分", "私"; "シロコ", "シロコちゃん" ],
        parseHtml html |> List.map (fun entry -> entry.Callee, entry.Name)
    )

[<Fact>]
let ``見出し行が無いテーブルも呼称表として読める`` () =
    // 一部のキャラクターのテーブルは見出し行を持たない。
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>カナエ</h4>"
        + "<table><tbody><tr><td>カナエ</td><td>自分</td><td>私</td></tr></tbody></table>"
        + "</div>"

    Assert.Equal("私", (List.exactlyOne (parseHtml html)).Name)

[<Fact>]
let ``Callerはセルではなくキャラクターのh4見出しから取る`` () =
    // キャラクター列のセルはアイコン画像や略称が入って揺れる。
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>スズメ亭の女将</h4>"
        + appellationTable (
            "<tr><td><img alt=\"女将_icon.png\"><br class=\"spacer\">女将</td>"
            + "<td>柴大将</td><td>大将</td></tr>"
        )
        + "</div>"

    Assert.Equal("スズメ亭の女将", (List.exactlyOne (parseHtml html)).Caller)

[<Fact>]
let ``存在しないページへの編集リンクは名前に混入しない`` () =
    // 相手のページが未作成の場合、名前の後ろへ「?」の編集リンクが付く。
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>マナミ</h4>"
        + appellationTable (
            "<tr><td>マナミ</td>"
            + "<td>ミリア<a href=\"./?cmd=edit&amp;page=%E3%83%9F%E3%83%AA%E3%82%A2\">?</a></td>"
            + "<td>ミリアさん</td></tr>"
        )
        + "</div>"

    Assert.Equal("ミリア", (List.exactlyOne (parseHtml html)).Callee)

[<Fact>]
let ``呼称が空の行は落ちる`` () =
    // 呼称のセルが空の行は記録が無いだけなのでレコードにしない。
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>ミスズ</h4>"
        + appellationTable (
            "<tr><td>ミスズ</td><td>自分</td><td></td></tr>"
            + "<tr><td>ミスズ</td><td>カンナ</td><td>公安局長</td></tr>"
        )
        + "</div>"

    Assert.Equal("カンナ", (List.exactlyOne (parseHtml html)).Callee)

[<Fact>]
let ``セル数が想定外の行はRowShapeErrorになる`` () =
    let html =
        "<div id=\"body\"><h2>学校</h2><h3>部活</h3><h4>ホシノ</h4>"
        + appellationTable "<tr><td>自分</td></tr>"
        + "</div>"

    let error = Assert.Throws<RowShapeError>(fun () -> parseHtml html |> ignore)

    match error :> exn with
    | RowShapeError(character, cellCount) ->
        Assert.Equal("ホシノ", character)
        Assert.Equal(1, cellCount)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``呼称を1件も得られない場合はEntryNotFoundになる`` () =
    let html = "<div id=\"body\"><h2>学校</h2></div>"
    Assert.Throws<EntryNotFound>(fun () -> parseHtml html |> ignore) |> ignore

[<Fact>]
let ``toReferenceMarkdownは階層見出しと括弧書きの注釈で組み立てる`` () =
    let entries =
        [ { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Caller = "ホシノ"
            Callee = "ノノミ"
            CalleeNote = None
            Name = "ノノミちゃん"
            Note = None }
          { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Caller = "ホシノ"
            Callee = "ノノミ"
            CalleeNote = None
            Name = "十六夜ノノミさん"
            Note = Some "初対面時" }
          { School = "連邦生徒会"
            Club = None
            Caller = "リン"
            Callee = "自分"
            CalleeNote = None
            Name = "私"
            Note = None } ]

    let markdown = toReferenceMarkdown entries

    Assert.Contains("## アビドス高等学校", markdown)
    Assert.Contains("### 対策委員会", markdown)
    Assert.Contains("#### ホシノ", markdown)
    Assert.Contains("| ホシノ | ノノミ | ノノミちゃん、十六夜ノノミさん(初対面時) |", markdown)
    // 部活の無い学校ではh3を挟まずキャラクターの見出しが続く。
    Assert.Contains("## 連邦生徒会", markdown)
    Assert.DoesNotContain("### \n", markdown)
    Assert.Contains("#### リン", markdown)

[<Fact>]
let ``toJsonはcamelCaseのキーとnullのoptionで直列化する`` () =
    let json =
        toJson
            { Source = "https://bluearchive.wikiru.jp/?example"
              Entries =
                [ { School = "連邦生徒会"
                    Club = None
                    Caller = "リン"
                    Callee = "自分"
                    CalleeNote = None
                    Name = "私"
                    Note = None } ] }

    Assert.Contains("\"source\": \"https://bluearchive.wikiru.jp/?example\"", json)
    Assert.Contains("\"school\": \"連邦生徒会\"", json)
    Assert.Contains("\"club\": null", json)
    Assert.Contains("\"caller\": \"リン\"", json)
    Assert.Contains("\"name\": \"私\"", json)

[<Fact>]
let ``toCallerMarkdownは指定キャラクターだけを相手と呼称の2列で組み立てる`` () =
    let entries =
        [ { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Caller = "ホシノ"
            Callee = "ノノミ"
            CalleeNote = None
            Name = "ノノミちゃん"
            Note = None }
          { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Caller = "ホシノ"
            Callee = "ノノミ"
            CalleeNote = None
            Name = "十六夜ノノミさん"
            Note = Some "初対面時" }
          // 他のキャラクターのレコードは出力に含まれない。
          { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Caller = "シロコ"
            Callee = "ホシノ"
            CalleeNote = None
            Name = "ホシノ"
            Note = None } ]

    Assert.Equal(
        "| 相手 | 呼称 |\n" + "| --- | --- |\n" + "| ノノミ | ノノミちゃん、十六夜ノノミさん(初対面時) |\n",
        toCallerMarkdown "ホシノ" entries
    )

[<Fact>]
let ``toCallerMarkdownは該当キャラクターが無いとCallerNotFoundになる`` () =
    let error =
        Assert.Throws<CallerNotFound>(fun () -> toCallerMarkdown "ユウカ" [] |> ignore)

    match error :> exn with
    | CallerNotFound caller -> Assert.Equal("ユウカ", caller)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``ofJsonはtoJsonの出力を同じDocumentへ読み戻す`` () =
    let document =
        { Source = "https://bluearchive.wikiru.jp/?example"
          Entries =
            [ { School = "アビドス高等学校"
                Club = Some "対策委員会"
                Caller = "ホシノ"
                Callee = "ノノミ"
                CalleeNote = Some "モモフレンズ"
                Name = "ノノミちゃん"
                Note = Some "初対面時" }
              { School = "連邦生徒会"
                Club = None
                Caller = "リン"
                Callee = "自分"
                CalleeNote = None
                Name = "私"
                Note = None } ] }

    Assert.Equal(document, ofJson (toJson document))
