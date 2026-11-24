module BluePrompt.Test.CharacterListSpec

open System
open Falco.Markup
open Xunit
open BluePrompt.CharacterList

/// 一覧の生徒1人分の行を組み立てる。
/// 先頭のセルがレアリティで、アイコンに個別ページへのリンクが張られ、
/// 末尾のセルには表の編集リンクが絶対URLで入る。
let private studentRow (rarity: string) (page: string) : XmlNode =
    Elem.tr
        []
        [ Elem.td [] [ Text.raw rarity ]
          Elem.td
              []
              [ Elem.a
                    [ Attr.href $"./?%s{Uri.EscapeDataString page}" ]
                    [ Elem.img [ Attr.alt $"%s{page}_icon.png" ] ] ]
          Elem.td [] [ Text.raw "帽子" ]
          Elem.td
              []
              [ Elem.a
                    [ Attr.href "https://bluearchive.wikiru.jp/?cmd=table_edit&amp;row=1" ]
                    [ Text.raw "編集" ] ] ]

/// 行の列を#bodyの中の表として文字列にする。
let private renderTable (rows: XmlNode list) : string =
    renderNode (Elem.div [ Attr.id "body" ] [ Elem.table [] [ Elem.tbody [] rows ] ])

[<Fact>]
let ``生徒の行から個別ページのページ名を表の順に取り出す`` () =
    let html = renderTable [ studentRow "★3" "ヒフミ（レーシング）"; studentRow "★1" "ラナ" ]
    Assert.Equal<string list>([ "ヒフミ（レーシング）"; "ラナ" ], parseHtml html)

[<Fact>]
let ``先頭のセルがレアリティではない行は読み飛ばす`` () =
    // ページ上部のナビゲーションは他の一覧へのリンクを並べた表で、生徒の行ではない。
    let navigation =
        Elem.tr
            []
            [ Elem.td
                  []
                  [ Elem.a [ Attr.href "./?%E5%AD%A6%E6%A0%A1%E5%88%A5" ] [ Text.raw "学校別" ] ] ]

    let heading =
        Elem.tr [] [ Elem.th [] [ Text.raw "レア" ]; Elem.th [] [ Text.raw "名前" ] ]

    let html = renderTable [ navigation; heading; studentRow "★2" "ユウカ" ]
    Assert.Equal<string list>([ "ユウカ" ], parseHtml html)

[<Fact>]
let ``生徒の行が1つも無ければEntryNotFoundを送出する`` () =
    // 構造が変わって全滅した時に、漏れが無いという結果にしない。
    let html = renderTable [ Elem.tr [] [ Elem.td [] [ Text.raw "名前" ] ] ]
    Assert.Throws<EntryNotFound>(fun () -> parseHtml html |> ignore) |> ignore
