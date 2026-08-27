/// ブルーアーカイブ攻略有志Wiki(bluearchive.wikiru.jp)固有のページ取得とナレッジ化。
module BluePrompt.Wikiru

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks

/// wikiruのページ名から記事URLを組み立てる。
let pageUri (pageName: string) : Uri =
    Uri $"https://bluearchive.wikiru.jp/?%s{Uri.EscapeDataString pageName}"

/// 折りたたみUI(rgn)全体のセレクタ。
/// contentQueryは丸ごと除去し、studentContentQueryは除去対象から外して中身を残す。
/// 両者が同じ表記を参照することで、表記の変更が片方だけに伝わって空振りすることを防ぐ。
let private collapsibleSelector = ".rgn-container"

/// 画像のセレクタ。
/// contentQueryは除去し、studentContentQueryは除去対象から外してaltのテキストへ置き換える。
let private imageSelector = "img"

/// どの抽出経路でも本文として扱わないノイズのセレクタ。
/// contentQueryとstructuredContentQueryの双方がこの値を参照することで、
/// セレクタの追加が片方だけに伝わって取りこぼしへ戻ることを防ぐ。
let private noiseSelectors =
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
      // 表示対象ではない要素。pandocのMarkdown化もDOMのTextContentの読み出しも、
      // タグを落としてもテキスト内容を本文へ混ぜることがあり、
      // wikiのプラグインや広告タグ由来のスクリプト片が成果物へ紛れ込む余地がある。
      "script"
      "style"
      "noscript"
      "template" ]

/// wikiruの記事からナレッジとして使う部分を抜き出す設定。
/// 本文(#body)と脚注(#note)を残し、サイトのヘッダ・サイドバー・フッタは含めない。
let contentQuery: Extract.ContentQuery =
    { ContentSelectors = [ "#body"; "#note" ]
      RemoveSelectors =
        [
          // 見出しごとのページ内アンカー(†)と編集リンク。
          "a.anchor_super"
          // セクション末尾のページ上部へ戻るリンク。
          ".jumpmenu"
          // 目次。Markdownでは見出しをそのまま辿れる。
          "table.toc"
          // 折りたたみUI。ページ内ジャンプの五十音索引やコメント欄ルールに使われていて、
          // ジャンプできないMarkdownではどちらも要らない。
          // 折りたたみに本文が入っている生徒個別ページはstudentContentQueryで扱う。
          collapsibleSelector
          // 表内などに埋め込まれた他ページの編集リンク。
          // リンクアンラップの後に「EDIT」という文字列だけが残ってノイズになる。
          "a[href*='cmd=edit']"
          // includeプラグインが差し込むinclude元ページへのリンク。
          // リンクアンラップの後にページ名だけの行が残り、直前の表の一部と誤読される。
          ".permalink"
          // 画像。lazyload用プレースホルダのdata URIしか取れずノイズになる。
          imageSelector ]
        @ noiseSelectors
      UnwrapLinks = true
      ReplaceImagesWithAlt = false
      FlattenTables = true }

/// 生徒個別ページ用の抽出設定。
/// 生徒ページでは絆ランクボーナスや絆ストーリー一覧やボイスといった本文が
/// 折りたたみ(rgn)の中に入っているため、折りたたみ全体は除去せず、
/// 開閉ボタンと「クリックで展開」のラベルだけを取り除いて中身を残す。
/// スキル成長素材のような表は素材を画像で表現しているため、
/// 画像は除去せずaltに入っている素材名のテキストへ置き換える。
let studentContentQuery: Extract.ContentQuery =
    { contentQuery with
        RemoveSelectors =
            [ ".rgn-button"; ".rgn-description" ]
            @ List.filter
                (fun selector -> selector <> collapsibleSelector && selector <> imageSelector)
                contentQuery.RemoveSelectors
        ReplaceImagesWithAlt = true }

/// pandocを通さずDOMを直接読むページ用の抽出設定。
/// キャラ呼称表と学校別キャラクター一覧のように、
/// テーブルをレコードへ構造化してからMarkdownを組み立てる経路で使う。
/// パースが読む#bodyと#noteだけを残し、
/// contentQueryと同じ理由でスクリプト片・広告タグ・コメント欄と投稿フォーム由来の、
/// 任意ユーザー入力がレコードへ紛れ込む余地を断つ。
/// 一方でパースはDOMの構造をそのまま読むため、Markdown化前提の変形は掛けない。
/// 以下の3つはどちらの経路も必要としているため、後から変えられない。
///
/// - リンクを外さない。
///   Appellationは脚注アンカー(note_super)のhref/titleと編集リンクの判定に使い、
///   Schoolはカードのaのhrefから生徒個別ページのページ名を復元するのに使う
/// - 画像をaltのテキストへ置き換えない。
///   Appellationではセル内のアイコンのTextContentが空で無害なだけだが、
///   Schoolではアイコンのaltが生徒名そのものなので、
///   置き換えるとカードの名前へ混ざってレアリティと名前の分解が壊れる
/// - テーブルを平坦化しない。
///   Appellationのrowspanも、Schoolのカードのセル内のbrも、それぞれのパースが自前で扱う
let structuredContentQuery: Extract.ContentQuery =
    { ContentSelectors = [ "#body"; "#note" ]
      RemoveSelectors = noiseSelectors
      UnwrapLinks = false
      ReplaceImagesWithAlt = false
      FlattenTables = false }

/// 生徒個別ページからナレッジとして残すセクションの見出し。
/// ゲーム内外の事実を載せているセクションだけを列挙するホワイトリスト。
/// 「小ネタ」はゲーム内外の事実のまとめで、事実には著作権が発生しないため、
/// 他の事実セクションと同じ扱いで残す(plugins/jp-wikiru-bluearchive/README.md)。
/// 「ゲームにおいて」「運用考察」などのwiki独自の解説・考察は
/// 著作権方針によりそのままの形では置かないため、
/// 列挙されていないセクションは落ちる。
/// 「スキル成長素材」と「贈り物」は素材や品物が画像で表現されていて、
/// 画像除去後は数量だけが残り事実として読めないため外している。
let studentSectionTitles: string list =
    [ "基本情報"; "スキル"; "固有武器"; "愛用品"; "能力解放"; "絆ランクボーナス"; "絆ストーリー"; "ボイス"; "小ネタ" ]

/// 生徒個別ページからrole-playスキルの参照として残すセクションの見出し。
/// 人格と話し方を示す基本情報とボイスだけを残し、
/// 性能データの参照はjp-wikiru-bluearchive側のスキルに任せる。
let rolePlaySectionTitles: string list = [ "基本情報"; "ボイス" ]

/// 最初の見出しより前を切り落とす。
/// wikiruの記事は本文が最初の見出しから始まり、
/// それより前は関連ページへのナビゲーションや注意書きで、ナレッジには要らない。
/// 見出しが無いMarkdownはそのまま返す。
let private trimPreamble (markdown: string) : string =
    match Regex.Match(markdown, @"^#{1,6} ", RegexOptions.Multiline) with
    | m when m.Success -> markdown[m.Index ..]
    | _ -> markdown

/// GFMの脚注定義行(「[^1]: 本文」)への一致。
let private footnoteDefinitionPattern = Regex @"^\[\^(\d+)\]: "

/// wikiruの脚注をGFMの脚注文法へ変換する。
/// 本文中の参照はリンクを外した後に「\*1」の形で残り、
/// 末尾の#note由来の定義は「\*1 本文」の行になっているので、
/// 定義行を「[^1]: 本文」へ、残った参照を「[^1]」へ書き換える。
/// Multiline下の\sは改行にもマッチして後続の段落を巻き込むため、
/// 空白は行内のものだけに限定して定義のマッチを1行へ閉じ込める。
let private convertFootnotes (markdown: string) : string =
    let withDefinitions =
        Regex.Replace(
            markdown,
            @"^\\\*(\d+)[^\S\r\n]+(.*?)[^\S\r\n]*$",
            "[^$1]: $2",
            RegexOptions.Multiline
        )

    Regex.Replace(withDefinitions, @"\\\*(\d+)", "[^$1]")

/// 連続する空行を1つへ潰し、前後の空白を落として末尾を改行1つで終える。
/// 行の削除を伴う整形の共通の仕上げ。
let private normalizeBlankLines (markdown: string) : string =
    Regex.Replace(markdown, @"\n{3,}", "\n\n").Trim() + "\n"

/// Markdownの見出しの最も深いレベル。
let private maxHeadingDepth = 6

/// 見出し行への一致。#の数を見出しの深さとして捕捉する。
let private headingDepthPattern = Regex @"^(#{1,6}) "

/// 空行と同じ意味しか持たない行への一致。
/// 実体参照のままの&nbsp;と、中身の無い引用。
/// 残すと見出しが中身を持つかの判断で本文として数えられてしまう。
let private blankLikeLinePattern =
    Regex(@"^[^\S\r\n]*(&nbsp;|>)[^\S\r\n]*$", RegexOptions.Multiline)

/// コメント欄の見出し行への一致。#の数を見出しの深さとして捕捉する。
let private commentHeadingPattern = Regex @"^(#{1,6}) コメント(フォーム)?[^\S\r\n]*$"

/// コメント欄の節を丸ごと落とす。
/// コメントそのものは後から読み込まれるので取得したHTMLには残らないが、
/// 投稿のルールを畳んで置いているページがあり、
/// 見出しだけを消すとルールの本文が直前の節の続きとして残ってしまう。
/// 節の終わりは同じ深さ以下の見出しで、そこから先はまた残す。
/// 脚注の定義はページの末尾に置かれてこの節へ紛れ込むため、
/// 本文への参照ごと落ちてしまわないように残す。
let private removeCommentSection (markdown: string) : string =
    let step (kept: string list, dropping: int option) (line: string) =
        match commentHeadingPattern.Match line with
        | m when m.Success -> kept, Some m.Groups[1].Value.Length
        | _ ->
            match dropping with
            | None -> line :: kept, None
            | Some depth ->
                match headingDepthPattern.Match line with
                | m when m.Success && m.Groups[1].Value.Length <= depth -> line :: kept, None
                | _ when footnoteDefinitionPattern.IsMatch line -> line :: kept, dropping
                | _ -> kept, dropping

    markdown.Split '\n'
    |> Array.fold step ([], None)
    |> fst
    |> List.rev
    |> String.concat "\n"

/// 中身を持たない見出しを消す。
/// 画像を並べただけの節は画像の除去で本文を失い、見出しだけが残る。
/// 見出しから次の見出しまでに本文が1行も無ければ、その見出しはもう何も指していない。
/// 小見出しが全て落ちて空になった上位の見出しも同じ判断で落ちるように、
/// 文書の末尾から見ていき、見出しの深さごとに中身の有無を持つ。
/// 添字を見出しの深さへそのまま合わせるため、配列の0番は使わない。
let private removeEmptyHeadings (markdown: string) : string =
    let step (line: string) (kept: string list, hasContent: bool array) =
        match headingDepthPattern.Match line with
        | m when m.Success ->
            let depth = m.Groups[1].Value.Length
            let keep = hasContent[depth]

            // この見出しで自身と同じ深さ以下の区間が終わるため、そこまでの中身は消化済みになる。
            // 残す場合はこの見出し自体が、より浅い見出しから見た中身になる。
            let next =
                Array.init hasContent.Length (fun index ->
                    if index >= depth then false
                    elif keep then true
                    else hasContent[index])

            (if keep then line :: kept else kept), next
        | _ ->
            let next =
                if String.IsNullOrWhiteSpace line then
                    hasContent
                else
                    Array.create hasContent.Length true

            line :: kept, next

    let initial = ([], Array.create (maxHeadingDepth + 1) false)

    Array.foldBack step (markdown.Split '\n') initial |> fst |> String.concat "\n"

/// 変換後Markdownの後始末。
/// 最初の見出しより前のナビゲーションを切り落とし、
/// 脚注をGFMの文法へ変換し、
/// コメント欄の節を落とし、
/// 外部リンクの跡として残った🌐アイコンを消し、
/// セル内改行の結合で挟んだ読点の前に残った空白を詰め、
/// 画像だけのリンク列の跡や孤立した読点として残った区切り文字だけの行を消し、
/// wikiのテンプレートが置いた編集者向けの案内と、画像を失った説明の行を消し、
/// 空行と同じ意味しか持たない行を消し、
/// それらの除去で中身を失った見出しを消し、連続する空行を1つへ潰す。
/// 脚注の変換を先に済ませるのは、
/// コメント欄の節から脚注の定義だけを拾い出す判定を、
/// 変換後の1つの表記だけで書けるようにするため。
let cleanupMarkdown (markdown: string) : string =
    let withoutCommentSection =
        removeCommentSection (convertFootnotes (trimPreamble markdown))

    // wikiのJavaScriptが外部リンクの中へ付け足す🌐アイコンは、
    // リンク外しの後にただの文字として残る。
    let withoutExternalLinkIcon =
        Regex.Replace(withoutCommentSection, @"[^\S\r\n]*🌐", "")

    // セル内改行の結合で挟んだ読点の前に、元の空白テキストノード由来の空白が残ることがある。
    // 日本語では読点の前に空白は置かないので詰める。
    let withoutSpaceBeforeComma =
        Regex.Replace(withoutExternalLinkIcon, @"[^\S\r\n]+(?=、)", "")

    // 画像だけのリンクを「/」で並べたナビゲーションは、
    // 画像除去とリンク外しの後に区切り文字だけの行になる。
    // セル内改行の結合で挟んだ読点も、続く要素が空行や空列の除去で消えると孤立する。
    // 空白は行内のものだけに限定して、行を跨いだ巻き込みを防ぐ。
    let withoutSeparatorRemnant =
        Regex.Replace(
            withoutSpaceBeforeComma,
            @"^[^\S\r\n]*([/、][^\S\r\n]*)+$",
            "",
            RegexOptions.Multiline
        )

    // ページのテンプレートが置いていく「〜を書く欄です。」は、
    // 記事を書く人へ向けた欄の案内で、生徒についての事実ではない。
    let withoutEditorNotice =
        Regex.Replace(
            withoutSeparatorRemnant,
            @"^[^\S\r\n]*.*を書く欄です。[^\S\r\n]*$",
            "",
            RegexOptions.Multiline
        )

    // 画像を並べただけの折りたたみに付いていた説明は、
    // 画像の除去で指すものを失い、ちびキャラの見出しだけが本文として残る。
    // 落とすのはキャプション由来の名詞句だけなので、句点を含む行は本文として残す。
    // 否定の文字クラスは改行にも当たるため、行を跨いだ巻き込みを防ぐ。
    let withoutChibiCaption =
        Regex.Replace(
            withoutEditorNotice,
            @"^[^\S\r\n]*ちびキャラ[^。\r\n]*$",
            "",
            RegexOptions.Multiline
        )

    let withoutBlankLikeLine = blankLikeLinePattern.Replace(withoutChibiCaption, "")

    normalizeBlankLines (removeEmptyHeadings withoutBlankLikeLine)

/// h2見出し行への一致。見出しのテキストを捕捉する。
let private sectionHeadingPattern = Regex @"^## +(.*?)\s*$"

/// h2見出しのホワイトリストでMarkdownのセクションを選別する。
/// h2の見出しがtitlesに載っているセクションだけを、h3以下の小見出しごと残す。
/// 最初のh2より前の部分はセクションに属さないのでそのまま残す。
/// 脚注定義は元のセクション位置に関わらず、
/// 残った本文から参照されているものだけを文書末尾へ集める。
/// 選別で参照ごと落ちた定義を残すと宙に浮いた脚注になるため。
let filterSections (titles: string list) (markdown: string) : string =
    let lines = markdown.Split '\n'

    let definitions, bodyLines =
        Array.partition (fun (line: string) -> footnoteDefinitionPattern.IsMatch line) lines

    // h2見出しの行で残すかどうかの状態が切り替わり、行の採否は常にその状態に従う。
    let filtered =
        bodyLines
        |> Array.fold
            (fun (kept, keeping) line ->
                let keeping =
                    match sectionHeadingPattern.Match line with
                    | m when m.Success -> List.contains m.Groups[1].Value titles
                    | _ -> keeping

                (if keeping then line :: kept else kept), keeping)
            ([], true)
        |> fst
        |> List.rev

    let body = String.concat "\n" filtered

    // 参照番号の集合を本文から一度だけ作り、定義側は集合参照で絞り込む。
    let referencedNumbers =
        Regex.Matches(body, @"\[\^(\d+)\]")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Set.ofSeq

    let referencedDefinitions =
        definitions
        |> Array.filter (fun definition ->
            Set.contains
                (footnoteDefinitionPattern.Match(definition).Groups[1].Value)
                referencedNumbers)

    let withDefinitions =
        if Array.isEmpty referencedDefinitions then
            body
        else
            body.TrimEnd() + "\n\n" + String.concat "\n" referencedDefinitions

    normalizeBlankLines withDefinitions

/// wikiruの記事ページをナレッジ用Markdownへ変換する。
/// pandocArgumentsでpandocの変換オプションを調整できる。
let fetchMarkdownWith (pandocArguments: string list) (pageName: string) : Task<string> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) contentQuery
        let! markdown = Pandoc.toMarkdownWithArguments pandocArguments html
        return cleanupMarkdown markdown
    }

/// wikiruの記事ページを既定のpandoc引数でナレッジ用Markdownへ変換する。
let fetchMarkdown (pageName: string) : Task<string> =
    fetchMarkdownWith Pandoc.defaultMarkdownArguments pageName

/// 出典URLからナレッジファイル先頭に付ける出典の表記を組み立てる。
/// リンクには渡されたURLをそのまま使い、ページ名を経由した再エンコードの往復をしない。
/// 表示するページ名はクエリのパーセントデコードで復元し、
/// クエリの無いURLはページ名を復元できないのでURL全体を表示名にする。
/// Uri.ToStringはパーセントエンコードを解いた表示用文字列を返すため、リンクにはAbsoluteUriを使う。
let sourceHeader (source: Uri) : string =
    let pageName =
        match source.Query.TrimStart '?' with
        | "" -> source.AbsoluteUri
        | query -> Uri.UnescapeDataString query

    $"出典: [%s{pageName} - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{source.AbsoluteUri})\n\n"

/// ページ名からナレッジファイル先頭に付ける出典の表記を組み立てる。
let knowledgeHeader (pageName: string) : string = sourceHeader (pageUri pageName)

/// 出力先の親ディレクトリを作ってからファイルへ書き出す。
let private writeFile (outputPath: string) (content: string) : Task<unit> =
    task {
        match Path.GetDirectoryName outputPath with
        | null
        | "" -> ()
        | directory -> Directory.CreateDirectory directory |> ignore

        do! File.WriteAllTextAsync(outputPath, content)
    }

/// wikiruの記事をMarkdown化し、出典ヘッダ付きのナレッジファイルとして書き出す。
/// スキルが参照するリファレンスファイルの生成の入口。
/// 整形は掛けず、書き出したパスを返す。
/// 一括生成では複数の書き出しを終えてからnix fmtを1回で掛けるため、
/// 整形の呼び出しは書き出しと分けてTargetが持つ。
let writeKnowledge (pageName: string) (outputPath: string) : Task<string list> =
    task {
        let! markdown = fetchMarkdown pageName
        do! writeFile outputPath (knowledgeHeader pageName + markdown)
        return [ outputPath ]
    }

/// wikiruの記事から抽出・変形した本文を、Markdown化せずHTMLのまま書き出す。
/// 抽出設定を調整する時にpandoc変換前の中間HTMLを確認するための入口。
/// 一覧ページと生徒個別ページで抽出設定が異なるため、確認したいクエリを受け取る。
let writeContentHtml
    (query: Extract.ContentQuery)
    (pageName: string)
    (outputPath: string)
    : Task<unit> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) query
        do! writeFile outputPath html
    }

/// wikiruの生徒個別ページを取得し、指定した見出しのセクションだけのMarkdownへ変換する。
/// 折りたたみの中身を残した設定で取得する。
let private fetchStudentSections (titles: string list) (pageName: string) : Task<string> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) studentContentQuery
        let! markdown = Pandoc.toMarkdown html
        return filterSections titles (cleanupMarkdown markdown)
    }

/// wikiruの生徒個別ページをナレッジ用Markdownへ変換する。
/// 事実を載せているセクションだけへ選別する。
let fetchStudentMarkdown (pageName: string) : Task<string> =
    fetchStudentSections studentSectionTitles pageName

/// 基本情報セクションの「入手方法」の見出し以降(入手方法・ステータス・装備)を切り落とす。
/// role-playの参照に要るのはプロフィールと紹介文だけで、
/// 性能データはrole-playには使わないため。
/// 「入手方法」は見出しプラグイン由来の太字行で、h3以下の見出しにはならない。
let trimGameplayDetails (markdown: string) : string =
    normalizeBlankLines (
        Regex.Replace(markdown, @"^\*\*入手方法\*\*[\s\S]*?(?=^## |\z)", "", RegexOptions.Multiline)
    )

/// wikiruの生徒個別ページをrole-playスキルの参照用Markdownへ変換する。
/// プロフィールと紹介文とボイスだけを残す。
let fetchRolePlayMarkdown (pageName: string) : Task<string> =
    task {
        let! markdown = fetchStudentSections rolePlaySectionTitles pageName
        return trimGameplayDetails markdown
    }

/// ナレッジ本体に節が1つも無かった時のページ名。
exception StudentSectionNotFound of pageName: string

/// ナレッジ本体に実際に含まれる節のh2の見出しを、現れる順に並べる。
/// 節を分けているのはh2なので、h3以下の小見出しは含めない。
/// studentSectionTitlesのホワイトリストをそのまま書くと、
/// 愛用品の節を持たないコトリ（応援団）のようなページで、
/// 存在しない節を存在すると宣言してしまう。
/// 読む側はその節を探し続けるか、抽出が壊れていると受け取ることになる。
/// filterSectionsは同じ見出しが2回現れてもどちらも残すが、
/// これは何が載っているかを述べるための一覧なので、重複は現れる順を保ったまま潰す。
let sectionTitles (markdown: string) : string list =
    markdown.Split '\n'
    |> Array.choose (fun line ->
        match sectionHeadingPattern.Match line with
        | m when m.Success -> Some m.Groups[1].Value
        | _ -> None)
    |> List.ofArray
    |> List.distinct

/// 生徒スキルのSKILL.md全体を組み立てる。
/// 生徒1人分のナレッジは別ファイルへ分けるほどの量にならないため、
/// フロントマターと使い方の説明とナレッジ本体を1つのSKILL.mdに収める。
/// 事実を引くための参照専用で、ユーザがスラッシュコマンドとして呼ぶ意味が無いため、
/// user-invocable: falseでコマンドの一覧からは外す。
let studentSkillMarkdown (skillName: string) (pageName: string) (markdown: string) : string =
    // 節を1つも読み取れないまま組み立てると、
    // 何が載っているかを述べない壊れた説明のスキルを書き出すことになる。
    let sections =
        match sectionTitles markdown with
        | [] -> raise (StudentSectionNotFound pageName)
        | titles -> String.concat "・" titles

    // フロントマターのdescriptionは1行である必要があるため、ソース上でだけ分割して結合する。
    //
    // 節を列挙しないのは、どの節があるかがページごとに違うため。
    // 愛用品を持たない衣装もあり、固定で並べると本文が挙げない節をここだけが挙げることになる。
    // 日本語の節名をそのまま置ける文でもないので、
    // 引くかどうかの判断に足りる事実の種類だけを述べる。
    let description =
        String.concat
            " "
            [ $"Lookup facts about %s{pageName}, a Blue Archive student,"
              "such as profile, stats, skills, bond stories, voice lines and trivia."
              $"Use when answering questions about %s{pageName},"
              "checking the in-game performance data,"
              $"or role-playing scenes that involve %s{pageName}." ]

    $"""---
name: %s{skillName}
description: %s{description}
user-invocable: false
---

『ブルーアーカイブ』の生徒「%s{pageName}」のゲーム内の事実を調べるためのスキルです。

# データの構造

- セクションは%s{sections}です
- 縦に結合されたセルは各行に同じ内容が複製され、横に結合されたセルの残りは空になっています
- 別バージョン(衣装違い)の生徒は別のページなので、このデータには含まれません

# 使う時の注意

- データに無い情報は、別バージョンの生徒のスキルや出典のページで確認してください。似た名前の生徒の性能を混ぜないでください
- 小ネタはゲーム内外の事実のまとめです。一方、wiki執筆者による解説や運用考察は著作権方針により含めていません。必要な場合は出典のページを直接参照してください

# ナレッジ

%s{knowledgeHeader pageName}%s{markdown}"""

/// wikiruの生徒個別ページをMarkdown化し、SKILL.mdとして書き出す。
/// スキル名は出力先のディレクトリ名と一致する必要があるため、出力パスから導出する。
/// 整形は掛けず、書き出したパスを返す。
let writeStudentSkill (pageName: string) (outputPath: string) : Task<string list> =
    task {
        let skillName =
            match Path.GetDirectoryName outputPath with
            | null
            | "" ->
                raise (
                    ArgumentException($"スキル名を導出するディレクトリがありません: %s{outputPath}", nameof outputPath)
                )
            | directory -> Path.GetFileName directory

        let! markdown = fetchStudentMarkdown pageName
        do! writeFile outputPath (studentSkillMarkdown skillName pageName markdown)
        return [ outputPath ]
    }

/// wikiruの生徒個別ページをMarkdown化し、
/// role-playスキルが参照する衣装別の参照ファイルとして書き出す。
/// 整形は掛けず、書き出したパスを返す。
let writeRolePlayReference (pageName: string) (outputPath: string) : Task<string list> =
    task {
        let! markdown = fetchRolePlayMarkdown pageName
        do! writeFile outputPath (knowledgeHeader pageName + markdown)
        return [ outputPath ]
    }

/// wikiruのキャラ呼称表ページを構造化データへパースし、
/// LLM参照用のreference.mdと機械読み出し用のJSONを一度の取得から書き出す。
/// JSONをリポジトリへ併置することで、
/// 後段の生成処理がwikiruへ再アクセスせずに呼称を読み出せるようにする。
/// 整形は掛けず、書き出した2つのパスを返す。
let writeAppellation
    (pageName: string)
    (markdownPath: string)
    (jsonPath: string)
    : Task<string list> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) structuredContentQuery
        let entries = Appellation.parseHtml html

        do!
            writeFile
                markdownPath
                (knowledgeHeader pageName + Appellation.toReferenceMarkdown entries)

        let document: Appellation.Document =
            { Source = (pageUri pageName).AbsoluteUri
              Entries = entries }

        do! writeFile jsonPath (Appellation.toJson document)
        return [ markdownPath; jsonPath ]
    }

/// wikiruの学校別キャラクター一覧ページを構造化データへパースし、
/// LLM参照用のreference.mdを書き出す。
/// 整形は掛けず、書き出したパスを返す。
let writeSchool (pageName: string) (outputPath: string) : Task<string list> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) structuredContentQuery
        let entries = School.parseHtml html
        do! writeFile outputPath (knowledgeHeader pageName + School.toReferenceMarkdown entries)
        return [ outputPath ]
    }
