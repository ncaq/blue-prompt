/// 取得済みHTMLからの本文抽出。
/// ヘッダやサイドバーや広告を除いた本文だけをナレッジ用に抜き出す用途を想定している。
/// AngleSharpによる純粋なF#実装なので、ネットワークもブラウザも使わずに単体テストで検証できる。
module BluePrompt.Extract

open System.Text.RegularExpressions
open AngleSharp.Dom
open AngleSharp.Html.Parser

/// コンテンツ抽出でどのセレクタも要素に一致しなかった時のセレクタ一覧。
/// 抽出はHTML文字列だけで完結するため取得元の情報は持たない。
/// 取得元URLはPage側が包み直して付与する。
exception ContentNotFound of selectors: string list

/// コンテンツ抽出の設定。
type ContentQuery =
    {
        /// 抽出する要素のCSSセレクタ。
        /// 複数指定した場合はセレクタの列挙順に一致要素のHTMLを連結する。
        ContentSelectors: string list
        /// 抽出前にDOMから取り除く要素のCSSセレクタ。
        RemoveSelectors: string list
        /// aタグを外して中身だけ残すか。
        /// ページ内アンカーや相対リンクはページの外へ持ち出すと辿れず、ノイズにしかならない。
        UnwrapLinks: bool
        /// imgをalt属性のテキストへ置き換えるか。
        /// 素材や品物を画像で表現しているページでは、altに入っている名前が唯一の事実になる。
        /// altが空の画像と、altがただのファイル名でしかない画像は取り除く。
        /// falseの場合は何もしないので、画像を消したい時はRemoveSelectorsに"img"を入れる。
        ReplaceImagesWithAlt: bool
        /// テーブルのrowspan/colspanを個別セルへ展開し、セル内の改行やブロック要素を1行へ繋ぐか。
        /// GFMのパイプテーブルは結合セルもセル内改行も表現できず、
        /// pandocが変換を諦めてテーブル全体を[TABLE]という文字列へ潰してしまうため。
        FlattenTables: bool
    }

/// 一致した要素をDOMから取り除く。
let private removeElements (document: IDocument) (selector: string) : unit =
    for element in Seq.toArray (document.QuerySelectorAll selector) do
        element.Remove()

/// 一致した要素のタグを外して子ノードだけ残す。
let private unwrapElements (document: IDocument) (selector: string) : unit =
    for element in Seq.toArray (document.QuerySelectorAll selector) do
        element.Replace(Seq.toArray element.ChildNodes)

/// altがただのファイル名(画像拡張子で終わる)かどうかの判定。
let private fileNameAltPattern =
    Regex(@"\.(png|jpe?g|gif|webp|svg|avif)$", RegexOptions.IgnoreCase)

/// imgをalt属性のテキストへ置き換える。
/// altが空の画像と、altがただのファイル名の画像は取り除く。
let private replaceImagesWithAlt (document: IDocument) : unit =
    for image in Seq.toArray (document.QuerySelectorAll "img") do
        let alt =
            match image.GetAttribute "alt" with
            | null -> ""
            | alt -> alt.Trim()

        if alt = "" || fileNameAltPattern.IsMatch alt then
            image.Remove()
        else
            image.Replace [| document.CreateTextNode alt :> INode |]

/// パース済みDOMからqueryに従って本文だけをHTML文字列として抜き出す。
/// RemoveSelectorsの除去とReplaceImagesWithAlt・FlattenTables・UnwrapLinksの変形を、
/// この適用順で施した後に、
/// ContentSelectorsへ一致した要素のouterHTMLを連結して返す。
/// documentは変形で破壊的に書き換わる。
/// 一致する要素が無いセレクタは読み飛ばす。
/// wikiruの脚注のように存在しないことが正常な要素があるためで、
/// RemoveSelectorsの0件一致も同様に正常として扱う。
/// ただし全ContentSelectorsが1件も一致しなかった場合は、
/// サイト側の構造変更で抽出が全滅した可能性が高く、
/// 空のナレッジによる上書きを防ぐためContentNotFoundを送出する。
let contentHtmlOfDocument (query: ContentQuery) (document: IDocument) : string =
    for selector in query.RemoveSelectors do
        removeElements document selector

    if query.ReplaceImagesWithAlt then
        replaceImagesWithAlt document

    // 平坦化はセル内改行の結合で同じリンク先の折り返しを見分けるため、
    // リンクを外す前に行う。
    if query.FlattenTables then
        Table.flatten document

    if query.UnwrapLinks then
        unwrapElements document "a"

    let contents =
        query.ContentSelectors
        |> List.collect (fun selector ->
            document.QuerySelectorAll selector
            |> Seq.map (fun element -> element.OuterHtml)
            |> Seq.toList)

    if List.isEmpty contents then
        raise (ContentNotFound(selectors = query.ContentSelectors))

    String.concat "\n" contents

/// HTML文字列からqueryに従って本文だけをHTML文字列として抜き出す。
/// パースしてcontentHtmlOfDocumentへ委譲する。挙動と失敗条件はそちらと同じ。
/// 取得側が既にパース済みのDOMを持っている場合は、
/// 再パースを避けるためcontentHtmlOfDocumentを直接使う。
let contentHtml (query: ContentQuery) (html: string) : string =
    use document = HtmlParser().ParseDocument html
    contentHtmlOfDocument query document
