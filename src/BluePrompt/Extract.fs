/// 取得済みHTMLからの本文抽出。
/// ヘッダやサイドバーや広告を除いた本文だけをナレッジ用に抜き出す用途を想定している。
/// AngleSharpによる純粋なF#実装なので、ネットワークもブラウザも使わずに単体テストで検証できる。
module BluePrompt.Extract

open System
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
        /// 文書と同じサイト内へのaタグを外して中身だけ残すか。
        /// ページ内アンカーや相対リンクはページの外へ持ち出すと辿れず、
        /// サイト内の他ページへのリンクは数が多く本文を埋めてしまう。
        /// 他のサイトへのリンクは出典として辿れるように絶対URLにして残す。
        UnwrapInternalLinks: bool
        /// 画像のURLを文書のURLを基準にした絶対URLへ解決して残すか。
        /// 遅延読み込みの画像はsrcにプレースホルダのdata URIしか持たず、
        /// 本物のURLはdata-srcにあるため、data-srcがあればそちらを使う。
        /// http/httpsの絶対URLへ解決できない画像は辿れないので取り除く。
        /// falseの場合は何もしないので、画像を消したい時はRemoveSelectorsに"img"を入れる。
        ResolveImages: bool
        /// テーブルのrowspan/colspanを個別セルへ展開し、セル内の改行やブロック要素を1行へ繋ぐか。
        /// GFMのパイプテーブルは結合セルもセル内改行も表現できず、
        /// pandocが変換を諦めてテーブル全体を[TABLE]という文字列へ潰してしまうため。
        FlattenTables: bool
    }

/// 一致した要素をDOMから取り除く。
let private removeElements (document: IDocument) (selector: string) : unit =
    for element in Seq.toArray (document.QuerySelectorAll selector) do
        element.Remove()

/// 文書のURLを基準にhttp/httpsの絶対URLへ解決する。
/// 文字列からパースした文書のURLはabout:blankで、相対参照はhttp/httpsへ解決されずNoneになる。
let private resolveWebUri (document: IDocument) (reference: string) : Uri option =
    match Uri.TryCreate(document.BaseUri, UriKind.Absolute) with
    | true, baseUri ->
        match Uri.TryCreate(baseUri, reference.Trim()) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
            Some uri
        | _ -> None
    | _ -> None

/// 指定した名前以外の属性を全て外す。
/// Markdownで表せない属性は残しても変換で落ちるか、titleのようにファイル名を繰り返すだけになる。
let private keepOnlyAttribute (name: string) (element: IElement) : unit =
    for attribute in Seq.toArray element.Attributes do
        if attribute.Name <> name then
            element.RemoveAttribute attribute.Name |> ignore

/// 中身が同じURLの画像1つだけのリンクかどうか。
/// 画像を原寸で開くためのリンクで、画像のURLを繰り返すだけになる。
let private isSelfImageLink (uri: Uri) (link: IElement) : bool =
    match Seq.toList link.Children with
    | [ image ] ->
        image.LocalName = "img"
        && image.GetAttribute "src" = uri.AbsoluteUri
        && String.IsNullOrWhiteSpace link.TextContent
    | _ -> false

/// 他のサイトへのリンクだけhrefを絶対URLにして残し、それ以外のaタグは外して子ノードだけ残す。
/// 文書と同じホストか、http/httpsへ解決できないリンクと、
/// 同じURLの画像を包んでいるだけのリンクを外す。
/// 埋め込みのツイートのように中身を持たないリンクは、
/// そのままでは何も指せない空のリンクになるため、URLを表示文字にする。
/// 画像を包むリンクの判定は解決済みのsrcと比べるため、画像の変形より後に呼ぶ。
let private unwrapInternalLinks (document: IDocument) : unit =
    let documentHost =
        match Uri.TryCreate(document.BaseUri, UriKind.Absolute) with
        | true, uri -> uri.Host
        | _ -> ""

    for link in Seq.toArray (document.QuerySelectorAll "a") do
        let external =
            match link.GetAttribute "href" with
            | null -> None
            | href ->
                resolveWebUri document href
                |> Option.filter (fun uri ->
                    uri.Host <> documentHost && not (isSelfImageLink uri link))

        match external with
        | Some uri ->
            keepOnlyAttribute "href" link
            link.SetAttribute("href", uri.AbsoluteUri)

            if String.IsNullOrWhiteSpace link.TextContent && isNull (link.QuerySelector "img") then
                link.TextContent <- uri.AbsoluteUri
        | None -> link.Replace(Seq.toArray link.ChildNodes)

/// 画像の実体のURLの参照。
/// 遅延読み込みの画像はsrcにプレースホルダのdata URIを置き、本物をdata-srcに持つ。
let private imageReference (image: IElement) : string option =
    [ image.GetAttribute "data-src"; image.GetAttribute "src" ]
    |> List.tryFind (fun reference ->
        not (String.IsNullOrWhiteSpace reference)
        && not (reference.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)))

/// imgのURLを絶対URLへ解決し、srcとaltだけを持つ要素にする。
/// http/httpsの絶対URLへ解決できない画像は取り除く。
let private resolveImageSources (document: IDocument) : unit =
    for image in Seq.toArray (document.QuerySelectorAll "img") do
        match imageReference image |> Option.bind (resolveWebUri document) with
        | Some uri ->
            keepOnlyAttribute "alt" image
            image.SetAttribute("src", uri.AbsoluteUri)
        | None -> image.Remove()

/// 見出しのテキストを、ページ内アンカー(†)と編集リンクを除いて取り出す。
/// wikiruの見出しにはアンカーと編集リンクが同じ要素の中へ並んでいて、
/// TextContentをそのまま読むと見出しの一部として混ざる。
/// DOMを直接読むパースが見出しを状態として辿るために使う。
let headingText (heading: IElement) : string =
    heading.ChildNodes
    |> Seq.choose (fun node ->
        match node with
        | :? IElement as element when element.ClassList.Contains "anchor_super" -> None
        | node -> Some node.TextContent)
    |> String.concat ""
    |> _.Trim()

/// パース済みDOMからqueryに従って本文だけをHTML文字列として抜き出す。
/// RemoveSelectorsの除去とResolveImages・FlattenTables・UnwrapInternalLinksの変形を、
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

    if query.ResolveImages then
        resolveImageSources document

    // 平坦化はセル内改行の結合で同じリンク先の折り返しを見分けるため、
    // リンクを外す前に行う。
    if query.FlattenTables then
        Table.flatten document

    if query.UnwrapInternalLinks then
        unwrapInternalLinks document

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
