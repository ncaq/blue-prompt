module BluePrompt.Test.AppellationSpec

open Falco.Markup
open Xunit
open BluePrompt.Appellation
open BluePrompt.Test.HtmlFixture

/// 呼称表のテーブルを見出し行付きで組み立てる。
let private appellationTable (rows: XmlNode list) : XmlNode =
    Elem.table
        []
        [ Elem.thead
              []
              [ Elem.tr
                    []
                    [ Elem.th [] [ Text.raw "キャラクター" ]
                      Elem.th [] [ Text.raw "相手" ]
                      Elem.th [] [ Text.raw "呼称" ] ] ]
          Elem.tbody [] rows ]

/// 本文を包む#body。
let private bodyDiv (content: XmlNode list) : XmlNode = Elem.div [ Attr.id "body" ] content

/// 脚注の定義が並ぶ#body外の#note。
let private noteDiv (content: XmlNode list) : XmlNode = Elem.div [ Attr.id "note" ] content

/// キャラクター1人分の呼称表を、見出しの階層を添えて#bodyへ収める。
/// 学校と部活の名前は呼称の読み取りに関わらないため固定する。
let private characterBody (character: string) (rows: XmlNode list) : XmlNode =
    bodyDiv [ Text.h2 "学校"; Text.h3 "部活"; Text.h4 character; appellationTable rows ]

/// #bodyだけからなるページのHTML。
let private renderCharacterPage (character: string) (rows: XmlNode list) : string =
    renderNode (characterBody character rows)

[<Fact>]
let ``見出しの階層が学校と部活とキャラクターへ対応付く`` () =
    let html =
        renderNode (
            bodyDiv
                [ Elem.h2
                      []
                      [ Text.raw "アビドス高等学校 "
                        Elem.a [ Attr.class' "anchor_super"; Attr.href "#a" ] [ Text.raw "†" ] ]
                  Text.h3 "対策委員会"
                  Text.h4 "ホシノ"
                  appellationTable
                      [ Elem.tr
                            []
                            [ Elem.td [] [ Text.raw "ホシノ" ]
                              Elem.td [] [ Text.raw "自分" ]
                              Elem.td [] [ Text.raw "私" ] ] ] ]
        )

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
        renderNode (
            bodyDiv
                [ Text.h2 "連邦生徒会"
                  Text.h4 "リン"
                  appellationTable
                      [ Elem.tr
                            []
                            [ Elem.td [] [ Text.raw "リン" ]
                              Elem.td [] [ Text.raw "自分" ]
                              Elem.td [] [ Text.raw "私" ] ] ] ]
        )

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("連邦生徒会", entry.School)
    Assert.Equal(None, entry.Club)

[<Fact>]
let ``呼称は読点とbr要素のどちらでも区切られる`` () =
    let html =
        renderCharacterPage
            "ホシノ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "ホシノ" ]
                    Elem.td [] [ Text.raw "自分" ]
                    Elem.td [] [ Text.raw "私、おじさん"; spacerBreak; Text.raw "わし" ] ] ]

    Assert.Equal<string list>([ "私"; "おじさん"; "わし" ], parseHtml html |> List.map _.Name)

[<Fact>]
let ``脚注は#noteの定義から呼称の注釈として解決される`` () =
    let html =
        renderSiblings
            [ characterBody
                  "ホシノ"
                  [ Elem.tr
                        []
                        [ Elem.td [] [ Text.raw "ホシノ" ]
                          Elem.td [] [ Elem.a [ Attr.href "#Nonomi" ] [ Text.raw "ノノミ" ] ]
                          Elem.td
                              []
                              [ Text.raw "ノノミちゃん"
                                spacerBreak
                                Text.raw "十六夜ノノミさん"
                                Elem.a
                                    [ Attr.id "notetext_1"
                                      Attr.href "#notefoot_1"
                                      Attr.class' "note_super"
                                      Attr.title "初対面時" ]
                                    [ Text.raw "*1" ] ] ] ]
              noteDiv
                  [ Elem.a
                        [ Attr.id "notefoot_1"; Attr.href "#notetext_1"; Attr.class' "note_super" ]
                        [ Text.raw "*1" ]
                    Elem.span [ Attr.class' "small" ] [ Text.raw "初対面のとき" ]
                    Elem.br [] ] ]

    // 本文へ*1の文字が混入せず、注釈はtitle属性ではなく#noteの定義を正とする。
    Assert.Equal<(string * string option) list>(
        [ "ノノミちゃん", None; "十六夜ノノミさん", Some "初対面のとき" ],
        parseHtml html |> List.map (fun entry -> entry.Name, entry.Note)
    )

[<Fact>]
let ``脚注の定義を辿れない場合はtitle属性で代替する`` () =
    let html =
        renderCharacterPage
            "ホシノ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "ホシノ" ]
                    Elem.td [] [ Text.raw "ヒフミ" ]
                    Elem.td
                        []
                        [ Text.raw "ファウストさん"
                          Elem.a
                              [ Attr.href "#notefoot_9"
                                Attr.class' "note_super"
                                Attr.title "覆面時" ]
                              [ Text.raw "*9" ] ] ] ]

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal(Some "覆面時", entry.Note)

[<Fact>]
let ``脚注定義の本文は次の脚注アンカーを巻き込まない`` () =
    // 脚注定義の区切りのbrが欠けたマークアップでも、
    // 次の脚注の定義が前の脚注の本文へ混入してはいけない。
    let noteAnchor (index: int) : XmlNode =
        Elem.a
            [ Attr.id $"notefoot_%d{index}"
              Attr.href $"#notetext_%d{index}"
              Attr.class' "note_super" ]
            [ Text.raw $"*%d{index}" ]

    let html =
        renderSiblings
            [ characterBody
                  "ホシノ"
                  [ Elem.tr
                        []
                        [ Elem.td [] [ Text.raw "ホシノ" ]
                          Elem.td [] [ Text.raw "ノノミ" ]
                          Elem.td
                              []
                              [ Text.raw "十六夜ノノミさん"
                                Elem.a
                                    [ Attr.href "#notefoot_1"; Attr.class' "note_super" ]
                                    [ Text.raw "*1" ] ] ] ]
              noteDiv
                  [ noteAnchor 1
                    Elem.span [ Attr.class' "small" ] [ Text.raw "初対面のとき" ]
                    noteAnchor 2
                    Elem.span [ Attr.class' "small" ] [ Text.raw "変装中" ]
                    Elem.br [] ] ]

    Assert.Equal(Some "初対面のとき", (List.exactlyOne (parseHtml html)).Note)

[<Fact>]
let ``相手の名前に付いた脚注はCalleeNoteとして解決される`` () =
    let html =
        renderCharacterPage
            "ノノミ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "ノノミ" ]
                    Elem.td
                        []
                        [ Text.raw "Mr.ニコライ"
                          Elem.a
                              [ Attr.href "#notefoot_10"
                                Attr.class' "note_super"
                                Attr.title "モモフレンズ" ]
                              [ Text.raw "*10" ] ]
                    Elem.td [] [ Text.raw "ミスター・ニコライ" ] ] ]

    let entry = List.exactlyOne (parseHtml html)
    Assert.Equal("Mr.ニコライ", entry.Callee)
    Assert.Equal(Some "モモフレンズ", entry.CalleeNote)

[<Fact>]
let ``キャラクターの見出しより前にあるテーブルは読み飛ばされる`` () =
    // ページ冒頭の目次テーブルはどのキャラクターにも属さない。
    let html =
        renderNode (
            bodyDiv
                [ Elem.table [] [ Elem.tr [] [ Elem.td [] [ Text.raw "Table of Contents" ] ] ]
                  Text.h2 "学校"
                  Text.h3 "部活"
                  Text.h4 "ホシノ"
                  appellationTable
                      [ Elem.tr
                            []
                            [ Elem.td [] [ Text.raw "ホシノ" ]
                              Elem.td [] [ Text.raw "自分" ]
                              Elem.td [] [ Text.raw "私" ] ] ] ]
        )

    Assert.Equal("私", (List.exactlyOne (parseHtml html)).Name)

[<Fact>]
let ``rowspanで結合されたキャラクター列の継続行も相手と呼称として読める`` () =
    // キャラクター列は先頭行だけに置かれ、2行目以降は相手と呼称の2セルになる。
    let html =
        renderCharacterPage
            "ホシノ"
            [ Elem.tr
                  []
                  [ Elem.td [ Attr.rowspan "2" ] [ Text.raw "ホシノ" ]
                    Elem.td [] [ Text.raw "自分" ]
                    Elem.td [] [ Text.raw "私" ] ]
              Elem.tr [] [ Elem.td [] [ Text.raw "シロコ" ]; Elem.td [] [ Text.raw "シロコちゃん" ] ] ]

    Assert.Equal<(string * string) list>(
        [ "自分", "私"; "シロコ", "シロコちゃん" ],
        parseHtml html |> List.map (fun entry -> entry.Callee, entry.Name)
    )

[<Fact>]
let ``見出し行が無いテーブルも呼称表として読める`` () =
    // 一部のキャラクターのテーブルは見出し行を持たない。
    let html =
        renderNode (
            bodyDiv
                [ Text.h2 "学校"
                  Text.h3 "部活"
                  Text.h4 "カナエ"
                  Elem.table
                      []
                      [ Elem.tr
                            []
                            [ Elem.td [] [ Text.raw "カナエ" ]
                              Elem.td [] [ Text.raw "自分" ]
                              Elem.td [] [ Text.raw "私" ] ] ] ]
        )

    Assert.Equal("私", (List.exactlyOne (parseHtml html)).Name)

[<Fact>]
let ``Callerはセルではなくキャラクターのh4見出しから取る`` () =
    // キャラクター列のセルはアイコン画像や略称が入って揺れる。
    let html =
        renderCharacterPage
            "スズメ亭の女将"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Elem.img [ Attr.alt "女将_icon.png" ]; spacerBreak; Text.raw "女将" ]
                    Elem.td [] [ Text.raw "柴大将" ]
                    Elem.td [] [ Text.raw "大将" ] ] ]

    Assert.Equal("スズメ亭の女将", (List.exactlyOne (parseHtml html)).Caller)

[<Fact>]
let ``存在しないページへの編集リンクは名前に混入しない`` () =
    // 相手のページが未作成の場合、名前の後ろへ「?」の編集リンクが付く。
    let html =
        renderCharacterPage
            "マナミ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "マナミ" ]
                    Elem.td
                        []
                        [ Text.raw "ミリア"
                          Elem.a
                              [ Attr.href "./?cmd=edit&page=%E3%83%9F%E3%83%AA%E3%82%A2" ]
                              [ Text.raw "?" ] ]
                    Elem.td [] [ Text.raw "ミリアさん" ] ] ]

    Assert.Equal("ミリア", (List.exactlyOne (parseHtml html)).Callee)

[<Fact>]
let ``呼称が空の行は落ちる`` () =
    // 呼称のセルが空の行は記録が無いだけなのでレコードにしない。
    let html =
        renderCharacterPage
            "ミスズ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "ミスズ" ]; Elem.td [] [ Text.raw "自分" ]; Elem.td [] [] ]
              Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "ミスズ" ]
                    Elem.td [] [ Text.raw "カンナ" ]
                    Elem.td [] [ Text.raw "公安局長" ] ] ]

    Assert.Equal("カンナ", (List.exactlyOne (parseHtml html)).Callee)

[<Fact>]
let ``セル内に入れ子のテーブルがあっても外側の行だけが読まれる`` () =
    // trの走査が子孫全体に及ぶと、内側のテーブルの行が外側の行としても読まれ、
    // セル数の食い違いでRowShapeErrorになるか同じ行が二重にレコード化される。
    // さらに#body tableのセレクタは入れ子のテーブル自体にも一致するため、
    // 独立したテーブルとして重ねて読まれる経路もある。
    let nestedTable = Elem.table [] [ Elem.tr [] [ Elem.td [] [] ] ]

    let html =
        renderCharacterPage
            "ホシノ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "ホシノ" ]
                    Elem.td [] [ Text.raw "シロコ" ]
                    Elem.td [] [ Text.raw "シロコちゃん"; nestedTable ] ] ]

    Assert.Equal("シロコちゃん", (List.exactlyOne (parseHtml html)).Name)

[<Fact>]
let ``完全に同じ内容の行はレコードとしては1件になる`` () =
    // wiki側の編集ミスで同じ行が丸ごと2度書かれることがあり、
    // そのまま残すとJSONにもMarkdownにも同じ呼称が重複して出る。
    let html =
        renderCharacterPage
            "チナツ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "チナツ" ]
                    Elem.td [] [ Text.raw "セナ" ]
                    Elem.td [] [ Text.raw "セナ部長、部長" ] ]
              Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "チナツ" ]
                    Elem.td [] [ Text.raw "セナ" ]
                    Elem.td [] [ Text.raw "セナ部長、部長" ] ] ]

    Assert.Equal<string list>([ "セナ部長"; "部長" ], parseHtml html |> List.map _.Name)

[<Fact>]
let ``同じ呼称が複数行に現れても1件にまとまり差分のある呼称は残る`` () =
    // 行の一部だけが重なる場合も、重複除去は呼称のレコード単位で効く。
    let html =
        renderCharacterPage
            "チナツ"
            [ Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "チナツ" ]
                    Elem.td [] [ Text.raw "セナ" ]
                    Elem.td [] [ Text.raw "セナ部長、部長" ] ]
              Elem.tr
                  []
                  [ Elem.td [] [ Text.raw "チナツ" ]
                    Elem.td [] [ Text.raw "セナ" ]
                    Elem.td [] [ Text.raw "セナ部長、部長、先輩" ] ] ]

    Assert.Equal<string list>([ "セナ部長"; "部長"; "先輩" ], parseHtml html |> List.map _.Name)

[<Fact>]
let ``注釈だけが異なる呼称は別レコードとして残る`` () =
    // 重複除去はレコードの完全一致だけを潰す。
    // 注釈が違えば別の事実なので、まとめずに両方残す。
    let html =
        renderSiblings
            [ characterBody
                  "チナツ"
                  [ Elem.tr
                        []
                        [ Elem.td [] [ Text.raw "チナツ" ]
                          Elem.td [] [ Text.raw "セナ" ]
                          Elem.td
                              []
                              [ Text.raw "部長"
                                Elem.a
                                    [ Attr.class' "note_super"; Attr.href "#notefoot_1" ]
                                    [ Text.raw "*1" ] ] ]
                    Elem.tr
                        []
                        [ Elem.td [] [ Text.raw "チナツ" ]
                          Elem.td [] [ Text.raw "セナ" ]
                          Elem.td [] [ Text.raw "部長" ] ] ]
              noteDiv
                  [ Elem.a
                        [ Attr.id "notefoot_1"; Attr.class' "note_super"; Attr.href "#notetext_1" ]
                        [ Text.raw "*1" ]
                    Elem.span [] [ Text.raw "正式な役職" ]
                    Elem.br [] ] ]

    Assert.Equal<(string * string option) list>(
        [ "部長", Some "正式な役職"; "部長", None ],
        parseHtml html |> List.map (fun entry -> entry.Name, entry.Note)
    )

[<Fact>]
let ``セル数が想定外の行はRowShapeErrorになる`` () =
    let html = renderCharacterPage "ホシノ" [ Elem.tr [] [ Elem.td [] [ Text.raw "自分" ] ] ]

    let error = Assert.Throws<RowShapeError>(fun () -> parseHtml html |> ignore)

    match error :> exn with
    | RowShapeError(character, cellCount) ->
        Assert.Equal("ホシノ", character)
        Assert.Equal(1, cellCount)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``呼称を1件も得られない場合はEntryNotFoundになる`` () =
    let html = renderNode (bodyDiv [ Text.h2 "学校" ])
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
