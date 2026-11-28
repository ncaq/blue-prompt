/// wikiruのキャラクター一覧の読み取り。
/// 実装済みの生徒を衣装違いも含めて1行ずつ並べた表で、
/// 学校別キャラクター一覧には現れない衣装違いのページ名をここから得る。
/// マニフェストの生徒の一覧に漏れが無いかを確かめるために使う。
/// パースは渡されたDOMを読むだけで自分では取得しないため、
/// HTML文字列から組んだDOMを渡せば、ネットワークを使わずに単体テストで検証できる。
module BluePrompt.CharacterList

open System
open AngleSharp.Dom
open AngleSharp.Html.Parser

/// 一覧から1件も生徒を得られなかった時。
/// サイト側の構造変更でパースが全滅した可能性が高く、
/// 「一覧に無いので漏れも無い」という誤った結果を返さないために止める。
exception EntryNotFound

/// 生徒の行かどうか。
/// 生徒の行は先頭のセルがレアリティで、見出しの行やページ上部のナビゲーションはそうではない。
let private isStudentRow (row: IElement) : bool =
    match row.QuerySelector "td" with
    | null -> false
    | first -> first.TextContent.Trim().StartsWith("★", StringComparison.Ordinal)

/// パース済みDOMから、一覧に並ぶ生徒個別ページのページ名を表の順に取り出す。
/// 行の中で最初に現れるページへのリンクが、アイコンに張られた生徒個別ページへのリンクになる。
/// 行の末尾の表の編集リンクは相対リンクではないので読まれない。
/// 編集フォームへのリンクはWikiruDom.resolveEditLinksで片付けたDOMを受ける。
/// 1件も得られなかった場合はEntryNotFoundを送出する。
let parse (document: IDocument) : string list =
    let pages =
        document.QuerySelectorAll "#body tr"
        |> Seq.filter isStudentRow
        |> Seq.choose (fun row -> row.QuerySelectorAll "a" |> Seq.tryPick WikiruDom.linkedPageName)
        |> List.ofSeq

    if List.isEmpty pages then
        raise EntryNotFound

    pages

/// HTML文字列から生徒個別ページのページ名を取り出す。
/// パースして、取得した時と同じく編集フォームへのリンクを片付けてから、parseへ委譲する。
/// 挙動と失敗条件はそちらと同じ。
let parseHtml (html: string) : string list =
    use document = HtmlParser().ParseDocument html
    WikiruDom.resolveEditLinks document
    parse document
