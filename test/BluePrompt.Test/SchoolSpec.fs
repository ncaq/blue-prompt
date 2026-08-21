module BluePrompt.Test.SchoolSpec

open System
open Xunit
open BluePrompt.School

/// 生徒1人分のカードを、ページと同じ1行1列のテーブルとして組み立てる。
/// レアリティ・アイコン画像・名前をbrで縦へ並べ、
/// アイコンと名前の双方が同じリンク先を指している。
let private cardTo (href: string) (rarity: string) (name: string) : string =
    $"""<div class="ie5"><table class="style_table"><tbody><tr>"""
    + $"""<td class="style_td">%s{rarity}<br class="spacer" />"""
    + $"""<a href="%s{href}"><img src="icon.png" alt="%s{name}" /></a><br class="spacer" />"""
    + $"""<a href="%s{href}"><span>%s{name}<br class="spacer" />&nbsp;</span></a>"""
    + "</td></tr></tbody></table></div>"

/// 生徒1人分のカードを、個別ページへのリンク付きで組み立てる。
let private card (rarity: string) (page: string) (name: string) : string =
    cardTo $"./?%s{Uri.EscapeDataString page}" rarity name

/// 本文の中身を#bodyで包む。
let private body (content: string) : string = $"""<div id="body">%s{content}</div>"""

[<Fact>]
let ``見出しの階層が学校と部活へ対応付く`` () =
    let html =
        body (
            "<h2>アビドス高等学校 <a class=\"anchor_super\" href=\"#a\">†</a></h2>"
            + "<h3>対策委員会</h3>"
            + card "★3" "ホシノ" "ホシノ"
        )

    Assert.Equal<Entry list>(
        [ { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Rarity = "★3"
            Name = "ホシノ"
            Page = None } ],
        parseHtml html
    )

[<Fact>]
let ``部活の見出しが無い学校ではClubはNoneになる`` () =
    // 連邦生徒会はh2直下にカードが並ぶ。
    let html = body ("<h2>連邦生徒会</h2>" + card "NPC" "リン" "リン")
    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("連邦生徒会", entry.School)
    Assert.Equal(None, entry.Club)

[<Fact>]
let ``学校が変わると部活の状態は持ち越されない`` () =
    let html =
        body (
            "<h2>アビドス高等学校</h2><h3>対策委員会</h3>"
            + card "★3" "ホシノ" "ホシノ"
            + "<h2>連邦生徒会</h2>"
            + card "NPC" "リン" "リン"
        )

    Assert.Equal<(string * string option) list>(
        [ "アビドス高等学校", Some "対策委員会"; "連邦生徒会", None ],
        parseHtml html |> List.map (fun entry -> entry.School, entry.Club)
    )

[<Fact>]
let ``名前はアイコンのaltではなくカードの表示名から読む`` () =
    // カイテンジャーの5人は同じページの別の節を指すため、ページ名では区別できない。
    let page = Uri.EscapeDataString "カイテンジャー"
    let href = $"./?%s{page}#red"

    let html = body ("<h2>所属不明・所属なし</h2><h3>カイテンジャー</h3>" + cardTo href "NPC" "カイテンレッド")

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("カイテンレッド", entry.Name)
    // 節へのリンクは節のidを落としてページ名だけを残す。
    Assert.Equal(Some "カイテンジャー", entry.Page)

[<Fact>]
let ``名前と同じページ名はPageに入らない`` () =
    let html = body ("<h2>連邦生徒会</h2>" + card "NPC" "スモモ" "スモモ")
    Assert.Equal(None, (List.exactlyOne (parseHtml html)).Page)

[<Fact>]
let ``節へのリンクでなくても名前と食い違うページ名はPageに入る`` () =
    let html =
        body ("<h2>所属不明・所属なし</h2><h3>カイテンジャー</h3>" + card "NPC" "カイテンジャー" "カイテンレッド")

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("カイテンレッド", entry.Name)
    Assert.Equal(Some "カイテンジャー", entry.Page)

[<Fact>]
let ``学校の見出しより前のセルは読み飛ばされる`` () =
    // ページ上部には他の一覧ページへのナビゲーションが置かれている。
    let html =
        body (
            "<table><tbody><tr><td>攻撃属性別</td></tr></tbody></table>"
            + "<h2>アビドス高等学校</h2><h3>対策委員会</h3>"
            + card "★3" "ホシノ" "ホシノ"
        )

    Assert.Equal<string list>([ "ホシノ" ], parseHtml html |> List.map _.Name)

[<Fact>]
let ``レアリティか名前を読めないセルはCellShapeErrorになる`` () =
    let html =
        body ("""<h2>学校</h2><table><tbody><tr><td class="style_td">★3</td></tr></tbody></table>""")

    let error = Assert.Throws<CellShapeError>(fun () -> parseHtml html |> ignore)

    match error :> exn with
    | CellShapeError text -> Assert.Equal("★3", text)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``生徒を1件も得られない場合はEntryNotFoundになる`` () =
    Assert.Throws<EntryNotFound>(fun () -> parseHtml (body "<h2>学校</h2>") |> ignore)
    |> ignore

[<Fact>]
let ``toReferenceMarkdownは学校ごとの1つのテーブルへまとめる`` () =
    let entries =
        [ { School = "アビドス高等学校"
            Club = Some "対策委員会"
            Rarity = "★3"
            Name = "ホシノ"
            Page = None }
          { School = "アビドス高等学校"
            Club = Some "アビドス生徒会"
            Rarity = "NPC"
            Name = "ユメ"
            Page = None }
          { School = "連邦生徒会"
            Club = None
            Rarity = "NPC"
            Name = "連邦生徒会長"
            Page = Some "スモモ" } ]

    Assert.Equal(
        "## アビドス高等学校\n"
        + "\n"
        + "| 部活・組織 | レアリティ | 生徒 |\n"
        + "| --- | --- | --- |\n"
        + "| 対策委員会 | ★3 | ホシノ |\n"
        + "| アビドス生徒会 | NPC | ユメ |\n"
        + "\n"
        + "## 連邦生徒会\n"
        + "\n"
        + "| 部活・組織 | レアリティ | 生徒 |\n"
        + "| --- | --- | --- |\n"
        // 部活の無い所属では部活のセルが空になり、
        // 名前と食い違うページ名は名前の後ろへ添える。
        + "|  | NPC | 連邦生徒会長(ページ名: スモモ) |\n",
        toReferenceMarkdown entries
    )

[<Fact>]
let ``toReferenceMarkdownはセルの縦棒をエスケープする`` () =
    let markdown =
        toReferenceMarkdown
            [ { School = "学校"
                Club = Some "部活|係"
                Rarity = "★3"
                Name = "名前|付き"
                Page = None } ]

    Assert.Contains("| 部活\\|係 | ★3 | 名前\\|付き |", markdown)
