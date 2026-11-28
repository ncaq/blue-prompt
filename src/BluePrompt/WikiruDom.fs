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
      // 表内などに埋め込まれた他ページの編集リンク。
      // リンクを外した後に「EDIT」という文字列だけが残ってノイズになる。
      "a[href*='cmd=edit']"
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

/// role-playスキルの参照用の組。
/// 生徒個別ページの組に加えて、取り消し線(del)を中身ごと取り除く。
/// 小ネタの取り消し線はwiki編集者の茶化しのツッコミで、
/// 残すと演じる生徒が自分への揶揄を自己認識として取り込んでしまう。
/// 記事の木へ読んだ後ではなくDOMの段で落とすのは、
/// 表のセルや改行を繋いだ後に抜くと、繋ぎの読点だけが跡として残るためである。
let rolePlayNoise: string list = "del" :: characterNoise

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

/// 未作成ページへの編集リンクかどうか。
/// wikiruは個別ページがまだ無いキャラクターを、
/// 名前の後ろへ編集リンクの「?」を添えた形で表示する。
/// 「?」は名前の一部ではなく、リンク先もページ名ではなく編集フォームなので、
/// 名前からもページ名からも除く。
let isEditLink (element: IElement) : bool =
    match element.GetAttribute "href" with
    | null -> false
    | href -> href.Contains "cmd=edit"

/// リンクの先のwikiruのページ名を取り出す。
/// ページへのリンクは「./?<パーセントエンコードされたページ名>」の相対リンクで、
/// 節へのリンクは末尾へ「#<節のid>」が付く。
/// 他のページの記法で書かれたリンクはページ名を復元できないのでNoneにする。
/// 未作成ページへの編集リンクはページ名ではなく編集フォームを指すので読まない。
let linkedPageName (anchor: IElement) : string option =
    /// ページへの相対リンク。パーセントエンコードされたままのページ名を返す。
    /// ページ名の中の`%23`がデコードで`#`へ戻ってから切られると、
    /// ページ名がそこで欠けるため、節の手前まででデコードより先に読み終える。
    let pageLink: Parse.TextParser<string> =
        pstring "./?" >>. manyChars (satisfy (fun c -> c <> '#'))

    match anchor.GetAttribute "href" with
    | _ when isEditLink anchor -> None
    | null -> None
    | href ->
        // ページ名が空のリンクは「名前(ページ名: )」という壊れた表記になるので読まない。
        match Parse.tryPrefix pageLink href |> Option.map Uri.UnescapeDataString with
        | Some(Str.NonEmpty page) -> Some page
        | _ -> None
