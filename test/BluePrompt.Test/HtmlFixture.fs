/// テストの入力にするHTMLをFalco.Markupで組み立てるための足場。
/// HTMLを文字列のリテラルで書くと、
/// 閉じタグの対応と入れ子の見通しの悪さで読み書きが難しくなるため、
/// 入力側のHTMLは全てFalco.Markupを通して組み立てる。
/// Falco.Markupは属性値もText.rawの本文もエスケープしないので、
/// 書いた通りのHTMLが出る。
/// wikiruが返すマークアップをそのまま置きたいので本文にはText.rawを使い、
/// エスケープするText.encは使わない。
/// `&amp;`のような実体参照も、実物に合わせてそのまま書く。
/// ここに置くのは要素を並べるだけでは書けないものと、
/// wikiru固有の決まったマークアップとして繰り返し現れるものに限り、
/// それ以外はElemとAttrとTextをそのまま呼ぶ。
/// 出力されたHTMLの文字列を検査する側は、
/// タグの途中や属性の断片も見るのでリテラルのままにする。
module BluePrompt.Test.HtmlFixture

open AngleSharp
open AngleSharp.Dom
open AngleSharp.Io
open Falco.Markup

/// wikiruが要素を縦へ並べる時に挟む改行。
let spacerBreak: XmlNode = Elem.br [ Attr.class' "spacer" ]

/// 兄弟として並ぶ要素をそのままHTMLの文字列にする。
let renderSiblings (nodes: XmlNode list) : string =
    nodes |> List.map renderNode |> String.concat ""

/// bodyの中身からHTML文書の文字列を組み立てる。
let renderDocument (body: XmlNode list) : string =
    renderNode (Elem.html [] [ Elem.body [] body ])

/// テストの文書の取得元として扱うURL。相対参照とサイト内の判定はここを基準に決まる。
let documentUrl = "https://bluearchive.wikiru.jp/?test"

/// bodyの中身からDOMを組み立てる。
/// 取得したDOMを受ける関数へ、取得を通さずに渡すための足場。
let parseDocument (body: XmlNode list) : IDocument =
    BrowsingContext
        .New(Configuration.Default)
        .OpenAsync(fun (response: VirtualResponse) ->
            response.Content(renderDocument body).Address(documentUrl) |> ignore)
        .GetAwaiter()
        .GetResult()

/// wikiruの記事の本文(#body)として中身を包んだDOMを組み立てる。
let parseBody (content: XmlNode list) : IDocument =
    parseDocument [ Elem.div [ Attr.id "body" ] content ]
