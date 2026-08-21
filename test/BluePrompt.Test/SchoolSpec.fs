module BluePrompt.Test.SchoolSpec

open System
open Falco.Markup
open Xunit
open BluePrompt.School
open BluePrompt.Test.HtmlFixture

/// 生徒1人分のカードの外枠を、ページと同じ1行1列のテーブルとして組み立てる。
let private cardFrame (content: XmlNode list) : XmlNode =
    Elem.div
        [ Attr.class' "ie5" ]
        [ Elem.table
              [ Attr.class' "style_table" ]
              [ Elem.tbody [] [ Elem.tr [] [ Elem.td [ Attr.class' "style_td" ] content ] ] ] ]

/// 生徒1人分のカードを組み立てる。
/// レアリティ・アイコン画像・名前をbrで縦へ並べ、
/// アイコンと名前の双方が同じリンク先を指している。
let private cardTo (href: string) (rarity: string) (name: string) : XmlNode =
    cardFrame
        [ Text.raw rarity
          spacerBreak
          Elem.a [ Attr.href href ] [ Elem.img [ Attr.src "icon.png"; Attr.alt name ] ]
          spacerBreak
          Elem.a
              [ Attr.href href ]
              [ Elem.span [] [ Text.raw name; spacerBreak; Text.raw "&nbsp;" ] ] ]

/// 生徒1人分のカードを、個別ページへのリンク付きで組み立てる。
let private card (rarity: string) (page: string) (name: string) : XmlNode =
    cardTo $"./?%s{Uri.EscapeDataString page}" rarity name

/// 本文の中身を#bodyで包む。
let private renderBody (content: XmlNode list) : string =
    renderNode (Elem.div [ Attr.id "body" ] content)

[<Fact>]
let ``見出しの階層が学校と部活へ対応付く`` () =
    let html =
        renderBody
            [ Elem.h2
                  []
                  [ Text.raw "アビドス高等学校 "
                    Elem.a [ Attr.class' "anchor_super"; Attr.href "#a" ] [ Text.raw "†" ] ]
              Text.h3 "対策委員会"
              card "★3" "ホシノ" "ホシノ" ]

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
    let html = renderBody [ Text.h2 "連邦生徒会"; card "NPC" "リン" "リン" ]
    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("連邦生徒会", entry.School)
    Assert.Equal(None, entry.Club)

[<Fact>]
let ``学校が変わると部活の状態は持ち越されない`` () =
    let html =
        renderBody
            [ Text.h2 "アビドス高等学校"
              Text.h3 "対策委員会"
              card "★3" "ホシノ" "ホシノ"
              Text.h2 "連邦生徒会"
              card "NPC" "リン" "リン" ]

    Assert.Equal<(string * string option) list>(
        [ "アビドス高等学校", Some "対策委員会"; "連邦生徒会", None ],
        parseHtml html |> List.map (fun entry -> entry.School, entry.Club)
    )

[<Fact>]
let ``名前はアイコンのaltではなくカードの表示名から読む`` () =
    // カイテンジャーの5人は同じページの別の節を指すため、ページ名では区別できない。
    let page = Uri.EscapeDataString "カイテンジャー"
    let href = $"./?%s{page}#red"

    let html =
        renderBody [ Text.h2 "所属不明・所属なし"; Text.h3 "カイテンジャー"; cardTo href "NPC" "カイテンレッド" ]

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("カイテンレッド", entry.Name)
    // 節へのリンクは節のidを落としてページ名だけを残す。
    Assert.Equal(Some "カイテンジャー", entry.Page)

[<Fact>]
let ``未作成ページの編集リンクは名前にもページ名にも入らない`` () =
    // wikiruは個別ページがまだ無いキャラクターを、名前の後ろへ編集リンクの「?」を添えて表示する。
    let page = Uri.EscapeDataString "ミリア"

    let html =
        renderBody
            [ Text.h2 "学校"
              Text.h3 "部活"
              cardFrame
                  [ Text.raw "★3"
                    Elem.br []
                    Elem.span
                        [ Attr.class' "noexists" ]
                        [ Text.raw "ミリア"
                          Elem.a [ Attr.href $"./?cmd=edit&page=%s{page}" ] [ Text.raw "?" ] ] ] ]

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("ミリア", entry.Name)
    Assert.Equal(None, entry.Page)

[<Fact>]
let ``名前と同じページ名はPageに入らない`` () =
    let html = renderBody [ Text.h2 "連邦生徒会"; card "NPC" "スモモ" "スモモ" ]
    Assert.Equal(None, (List.exactlyOne (parseHtml html)).Page)

[<Fact>]
let ``節へのリンクでなくても名前と食い違うページ名はPageに入る`` () =
    let html =
        renderBody [ Text.h2 "所属不明・所属なし"; Text.h3 "カイテンジャー"; card "NPC" "カイテンジャー" "カイテンレッド" ]

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("カイテンレッド", entry.Name)
    Assert.Equal(Some "カイテンジャー", entry.Page)

[<Fact>]
let ``ページ名の中のエンコードされた番号記号は節の区切りと混同されない`` () =
    // 節へのリンクの`#`とページ名の中の`%23`は、デコードより前でなければ見分けられない。
    let html = renderBody [ Text.h2 "学校"; card "NPC" "A#B" "表示名" ]

    Assert.Equal(Some "A#B", (List.exactlyOne (parseHtml html)).Page)

[<Fact>]
let ``ページ名が空のリンクはPageに入らない`` () =
    // 「名前(ページ名: )」という壊れた表記を出さない。
    let html = renderBody [ Text.h2 "学校"; cardTo "./?" "★3" "ホシノ" ]

    Assert.Equal(None, (List.exactlyOne (parseHtml html)).Page)

[<Fact>]
let ``相対リンク以外のhrefからはページ名を読まない`` () =
    // 絶対URLやInterWikiの記法ではページ名を復元できない。
    let href = "https://bluearchive.wikiru.jp/?" + Uri.EscapeDataString "ホシノ"
    let html = renderBody [ Text.h2 "学校"; cardTo href "★3" "ホシノ" ]

    Assert.Equal(None, (List.exactlyOne (parseHtml html)).Page)

[<Fact>]
let ``リンクを持たないカードのPageはNoneになる`` () =
    let html =
        renderBody [ Text.h2 "学校"; cardFrame [ Text.raw "★3"; Elem.br []; Text.raw "ホシノ" ] ]

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("ホシノ", entry.Name)
    Assert.Equal(None, entry.Page)

[<Fact>]
let ``学校の見出しより前のセルは読み飛ばされる`` () =
    // ページ上部には他の一覧ページへのナビゲーションが置かれている。
    let html =
        renderBody
            [ Elem.table [] [ Elem.tr [] [ Elem.td [] [ Text.raw "攻撃属性別" ] ] ]
              Text.h2 "アビドス高等学校"
              Text.h3 "対策委員会"
              card "★3" "ホシノ" "ホシノ" ]

    Assert.Equal<string list>([ "ホシノ" ], parseHtml html |> List.map _.Name)

[<Fact>]
let ``カードを並べる外枠のセルは読まれない`` () =
    // カードがレイアウト用のテーブルへ入ると、外枠のtdも同じ走査に掛かる。
    let html =
        renderBody
            [ Text.h2 "アビドス高等学校"
              Text.h3 "対策委員会"
              Elem.table
                  []
                  [ Elem.tr
                        []
                        [ Elem.td
                              [ Attr.class' "style_td" ]
                              [ card "★3" "ホシノ" "ホシノ"; card "★3" "シロコ" "シロコ" ] ] ] ]

    Assert.Equal<string list>([ "ホシノ"; "シロコ" ], parseHtml html |> List.map _.Name)

[<Fact>]
let ``学校の見出しより後ろのカード以外のテーブルはCellShapeErrorになる`` () =
    // 欠けた一覧で生成物を上書きしないため、読めないセルは黙って飛ばさず止まる。
    let html =
        renderBody
            [ Text.h2 "アビドス高等学校"
              Text.h3 "対策委員会"
              card "★3" "ホシノ" "ホシノ"
              Elem.table
                  []
                  [ Elem.tr [] [ Elem.td [ Attr.class' "style_td" ] [ Text.raw "注意書き" ] ] ] ]

    let error = Assert.Throws<CellShapeError>(fun () -> parseHtml html |> ignore)

    match error :> exn with
    | CellShapeError text -> Assert.Equal("注意書き", text)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``レアリティか名前を読めないセルはCellShapeErrorになる`` () =
    let rarityCell = Elem.td [ Attr.class' "style_td" ] [ Text.raw "★3" ]

    let html = renderBody [ Text.h2 "学校"; Elem.table [] [ Elem.tr [] [ rarityCell ] ] ]

    let error = Assert.Throws<CellShapeError>(fun () -> parseHtml html |> ignore)

    match error :> exn with
    | CellShapeError text -> Assert.Equal("★3", text)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``生徒を1件も得られない場合はEntryNotFoundになる`` () =
    Assert.Throws<EntryNotFound>(fun () -> parseHtml (renderBody [ Text.h2 "学校" ]) |> ignore)
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
