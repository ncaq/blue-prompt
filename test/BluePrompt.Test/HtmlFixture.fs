/// テストの入力にするHTMLをFalco.Markupで組み立てるための足場。
/// HTMLを文字列のリテラルで書くと、
/// 属性の引用符のエスケープと閉じタグの対応で読み書きが難しくなるため、
/// 入力側のHTMLは全てFalco.Markupを通して組み立てる。
/// ここに置くのは要素を並べるだけでは書けないものに限り、
/// 1つの要素で足りるものはElemとAttrとTextをそのまま呼ぶ。
/// 出力されたHTMLの文字列を検査する側は、
/// タグの途中や属性の断片も見るのでリテラルのままにする。
module BluePrompt.Test.HtmlFixture

open Falco.Markup

/// wikiruが要素を縦へ並べる時に挟む改行。
let spacerBreak: XmlNode = Elem.br [ Attr.class' "spacer" ]

/// 兄弟として並ぶ要素をそのままHTMLの文字列にする。
let renderSiblings (nodes: XmlNode list) : string =
    nodes |> List.map renderNode |> String.concat ""

/// bodyの中身からHTML文書の文字列を組み立てる。
let renderDocument (body: XmlNode list) : string =
    renderNode (Elem.html [] [ Elem.body [] body ])
