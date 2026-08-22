/// テストの入力にするHTMLをFalco.Markupで組み立てるための足場。
/// HTMLを文字列のリテラルで書くと、
/// 閉じタグの対応と入れ子の見通しの悪さで読み書きが難しくなるため、
/// 入力側のHTMLは全てFalco.Markupを通して組み立てる。
/// Falco.Markupは属性値もText.rawの本文もエスケープしないので、
/// 書いた通りのHTMLが出る。
/// wikiruが返すマークアップをそのまま置きたいので本文にはText.rawを使い、
/// エスケープするText.encは使わない。
/// ここに置くのは要素を並べるだけでは書けないものと、
/// wikiru固有の決まったマークアップとして繰り返し現れるものに限り、
/// それ以外はElemとAttrとTextをそのまま呼ぶ。
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
