/// ブルーアーカイブ攻略有志Wiki(bluearchive.wikiru.jp)のDOMの決まり。
/// どの要素が本文ではないのか、見出しやリンクをどう読むのかという、
/// このwikiのマークアップに固有の知識をここへ集める。
/// DOMからレコードへ直接読むパースと、記事の木への解釈の両方が使うため、それらより前に置く。
module BluePrompt.WikiruDom

open System
open AngleSharp.Dom
open XParsec
open XParsec.Parsers
open XParsec.CharParsers

/// 折りたたみUI(rgn)全体のセレクタ。
/// 記事用の組は丸ごと除去し、生徒ページ用の組は除去対象から外して中身を残す。
let private collapsibleSelector = ".rgn-container"

/// どの経路でも本文として扱わないノイズのセレクタ。
/// 全ての組がこの値を含むことで、セレクタの追加が片方だけに伝わって取りこぼしへ戻ることを防ぐ。
let private commonNoise =
    [
      // 広告。
      ".sticky-ads"
      "ins.adsbygoogle"
      // コメント欄と投稿フォームとその部品(絵文字ピッカーなど)。
      // wiki独自コンテンツは扱わない方針な上に、
      // 誰でも投稿できる欄由来の任意ユーザー入力を成果物へ入れないため。
      ".pcomment"
      "#pcomment-form"
      "div[class*='pcmt-']"
      // 表示対象ではない要素。TextContentの読み出しはタグを落としても文字を本文へ混ぜるので、
      // wikiのプラグインや広告タグ由来のスクリプト片が成果物へ紛れ込む余地がある。
      "script"
      "style"
      "noscript"
      "template"
      // フォーム部品。ネタバレ隠しプラグインが文字を隠すためのcheckboxとlabelを本文へ置く。
      "input"
      "label" ]

/// DOMからレコードへ直接読むページ用の組。
/// キャラ呼称表と学校別キャラクター一覧とキャラクター一覧のパースは、
/// リンクのhrefや脚注アンカーや表の結合をDOMのまま読むため、ノイズだけを除く。
let structuredNoise: string list = commonNoise

/// 記事を記事の木へ解釈する時の組。
let articleNoise: string list =
    [
      // 見出しごとのページ内アンカー(†)と編集リンク。
      "a.anchor_super"
      // セクション末尾のページ上部へ戻るリンク。
      ".jumpmenu"
      // 目次。Markdownでは見出しをそのまま辿れる。
      "table.toc"
      // 折りたたみUI。ページ内ジャンプの五十音索引やコメント欄ルールに使われていて、
      // ジャンプできないMarkdownではどちらも要らない。
      // 折りたたみに本文が入っている生徒個別ページはcharacterNoiseで扱う。
      collapsibleSelector
      // includeプラグインが差し込むinclude元ページへのリンク。
      // リンクを外した後にページ名だけの行が残り、直前の表の一部と誤読される。
      ".permalink" ]
    @ commonNoise

/// 生徒個別ページ用の組。
/// 生徒ページでは絆ランクボーナスや絆ストーリー一覧やボイスといった本文が
/// 折りたたみ(rgn)の中に入っているため、折りたたみ全体は除去せず、
/// 開閉ボタンと「クリックで展開」のラベルだけを取り除いて中身を残す。
let characterNoise: string list =
    [ ".rgn-button"; ".rgn-description" ]
    @ List.filter (fun selector -> selector <> collapsibleSelector) articleNoise

/// セレクタに一致した要素をDOMから取り除く。
/// 一致する要素が無いセレクタは読み飛ばす。存在しないことが正常な要素があるため。
let removeNoise (selectors: string list) (document: IDocument) : unit =
    for selector in selectors do
        for element in Seq.toArray (document.QuerySelectorAll selector) do
            element.Remove()

/// 見出しのテキストを、ページ内アンカー(†)と編集リンクを除いて取り出す。
/// wikiruの見出しにはアンカーと編集リンクが同じ要素の中へ並んでいて、
/// TextContentをそのまま読むと見出しの一部として混ざる。
let headingText (heading: IElement) : string =
    heading.ChildNodes
    |> Seq.choose (fun node ->
        match node with
        | :? IElement as element when element.ClassList.Contains "anchor_super" -> None
        | node -> Some node.TextContent)
    |> String.concat ""
    |> _.Trim()

/// 編集フォームへのリンクを片付ける。どの経路でも、DOMを読む前に掛ける。
/// 編集フォームへのリンクの扱いはここ1箇所で決めて、DOMを読む側はそれが残っていない前提で読む。
/// 読む側がそれぞれ読み飛ばす作りだと、片方だけが名前を落とすような食い違いが生まれる。
/// 編集フォームへのリンクには2つの種類があり、リンクの中身で見分ける。
///
/// - 編集のボタン。鉛筆の画像を持つリンクと、未作成ページの名前の後ろへ添えられる「?」だけのリンク。
///   本文ではないのでリンクごと取り除く
/// - 未作成ページの名前そのもの。wikiruは個別ページがまだ無い名前を、編集フォームへのリンクとして表示する。
///   リンクの先は要らないが名前は本文なので、リンクだけを外して文字を残す
///
/// 編集フォームへのリンクを一律に取り除くと、ページが作られるまでの間、その名前が本文から消える。
let resolveEditLinks (document: IDocument) : unit =
    for anchor in Seq.toArray (document.QuerySelectorAll "a[href*='cmd=edit']") do
        let isButton =
            not (isNull (anchor.QuerySelector "img")) || anchor.TextContent.Trim() = "?"

        if isButton then
            anchor.Remove()
        else
            anchor.Replace(Seq.toArray anchor.ChildNodes)

/// リンクの先のwikiruのページ名を取り出す。
/// ページへのリンクは「./?<パーセントエンコードされたページ名>」の相対リンクで、
/// 節へのリンクは末尾へ「#<節のid>」が付く。
/// 他のページの記法で書かれたリンクはページ名を復元できないのでNoneにする。
/// 編集フォームへのリンクはresolveEditLinksが先に片付けているので、ここへは来ない。
let linkedPageName (anchor: IElement) : string option =
    /// ページへの相対リンク。パーセントエンコードされたままのページ名を返す。
    /// ページ名の中の`%23`がデコードで`#`へ戻ってから切られると、
    /// ページ名がそこで欠けるため、節の手前まででデコードより先に読み終える。
    let pageLink: Parse.TextParser<string> =
        pstring "./?" >>. manyChars (satisfy (fun c -> c <> '#'))

    match anchor.GetAttribute "href" with
    | null -> None
    | href ->
        // ページ名が空のリンクは「名前(ページ名: )」という壊れた表記になるので読まない。
        match Parse.tryPrefix pageLink href |> Option.map Uri.UnescapeDataString with
        | Some(Str.NonEmpty page) -> Some page
        | _ -> None
