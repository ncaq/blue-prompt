/// ブルーアーカイブ攻略有志Wiki(bluearchive.wikiru.jp)固有のページ取得とナレッジ化。
module BluePrompt.Wikiru

open System
open System.IO
open System.Threading.Tasks
open XParsec
open XParsec.Parsers
open XParsec.CharParsers

/// wikiruのページ名から記事URLを組み立てる。
let pageUri (pageName: string) : Uri =
    Uri $"https://bluearchive.wikiru.jp/?%s{Uri.EscapeDataString pageName}"

/// 折りたたみUI(rgn)全体のセレクタ。
/// contentQueryは丸ごと除去し、studentContentQueryは除去対象から外して中身を残す。
/// 両者が同じ表記を参照することで、表記の変更が片方だけに伝わって空振りすることを防ぐ。
let private collapsibleSelector = ".rgn-container"

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
      "template"
      // フォーム部品。ネタバレ隠しプラグインが文字を隠すためのcheckboxとlabelを本文へ置く。
      // 箇条書きの項目に残るとpandocがブロックとみなしてリスト全体を段落へ崩し、
      // プロフィールのフルネームの項目が読めなくなる。
      "input"
      "label" ]

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
          ".permalink" ]
        @ noiseSelectors
      UnwrapInternalLinks = true
      // 画像は遅延読み込みのdata-srcから絶対URLを組み立てて残し、元の画像を辿れるようにする。
      ResolveImages = true
      FlattenTables = true
      // データの表は箇条書きにする。
      // パイプテーブルは整形の空白で膨らみ、Knowledgeのチャンクの境界で列の意味を失うため。
      TablesToLists = true }

/// 生徒個別ページ用の抽出設定。
/// 生徒ページでは絆ランクボーナスや絆ストーリー一覧やボイスといった本文が
/// 折りたたみ(rgn)の中に入っているため、折りたたみ全体は除去せず、
/// 開閉ボタンと「クリックで展開」のラベルだけを取り除いて中身を残す。
let studentContentQuery: Extract.ContentQuery =
    { contentQuery with
        RemoveSelectors =
            [ ".rgn-button"; ".rgn-description" ]
            @ List.filter
                (fun selector -> selector <> collapsibleSelector)
                contentQuery.RemoveSelectors }

/// role-playスキルの参照用の抽出設定。
/// 生徒個別ページの設定に加えて、取り消し線(del)を中身ごと取り除く。
/// 小ネタの取り消し線はwiki編集者の茶化しのツッコミで、
/// 残すと演じる生徒が自分への揶揄を自己認識として取り込んでしまう。
/// Markdownにすると取り消し線が行を跨ぐことがあるため、HTMLの段階で要素ごと落とす。
let rolePlayContentQuery: Extract.ContentQuery =
    { studentContentQuery with
        RemoveSelectors = "del" :: studentContentQuery.RemoveSelectors }

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
/// - 画像に手を加えない。
///   どちらのパースも画像を読まず、URLを解決しても使い道が無い
/// - テーブルを平坦化しない。
///   Appellationのrowspanも、Schoolのカードのセル内のbrも、それぞれのパースが自前で扱う
let structuredContentQuery: Extract.ContentQuery =
    { ContentSelectors = [ "#body"; "#note" ]
      RemoveSelectors = noiseSelectors
      UnwrapInternalLinks = false
      ResolveImages = false
      FlattenTables = false
      TablesToLists = false }

/// 生徒個別ページからナレッジとして残すセクションの見出し。
/// ゲーム内外の事実を載せているセクションだけを列挙するホワイトリスト。
/// 「小ネタ」はゲーム内外の事実のまとめで、事実には著作権が発生しないため、
/// 他の事実セクションと同じ扱いで残す(plugins/jp-wikiru-bluearchive/README.md)。
/// 「ゲームにおいて」「運用考察」などのwiki独自の解説・考察は
/// 著作権方針によりそのままの形では置かないため、
/// 列挙されていないセクションは落ちる。
let studentSectionTitles: string list =
    [ "基本情報"; "スキル"; "固有武器"; "愛用品"; "能力解放"; "絆ランクボーナス"; "絆ストーリー"; "ボイス"; "小ネタ" ]

/// 生徒個別ページからrole-playスキルの参照として残すセクションの見出し。
/// 人格と話し方を示す基本情報とボイスに、
/// 衣装ごとの出来事や人柄を伝える小ネタを加えて残し、
/// 性能データの参照はjp-wikiru-bluearchive側のスキルに任せる。
/// 絆ストーリーはタイトルと報酬の表、メモリアルロビーはBGMとスタッフの表しか持たず、
/// タイトルだけでは中身の作り話を誘うため外している。
let rolePlaySectionTitles: string list = [ "基本情報"; "ボイス"; "小ネタ" ]

/// プレイアブルではない登場人物のページからナレッジとして残すセクションの見出し。
/// 生徒個別ページと同じ形のページが多いので生徒のホワイトリストを土台にして、
/// 生徒のページには無い事実のセクションだけを足す。
/// アロナとプラナとソラはゲーム内のボイスを「ボイス集」の節に持つ。
/// カイテンジャーは5人で1ページで、メンバーごとのプロフィールと紹介文がそれぞれ1つの節になっている。
/// 「公式ツイッターの紹介」は埋め込みのツイートだけで、抽出すると中身が残らないため含めない。
let unplayableSectionTitles: string list =
    studentSectionTitles
    @ [ "ボイス集"; "カイテンレッド"; "カイテンブラック"; "カイテングリーン"; "カイテンイエロー"; "カイテンピンク" ]

/// wikiruの脚注の参照。リンクを外した後に「\*1」の形で残る。番号を返す。
let private wikiFootnoteMark: Parse.TextParser<string> =
    pstring "\\*" >>. Parse.digits

/// GFMの脚注の参照(「[^1]」)。番号を返す。
let private footnoteReference: Parse.TextParser<string> =
    pstring "[^" >>. Parse.digits .>> pchar ']'

/// GFMの脚注定義行(「[^1]: 本文」)の先頭。番号を返す。
let private footnoteDefinition: Parse.TextParser<string> =
    footnoteReference .>> pstring ": "

/// wikiruの脚注をGFMの脚注文法へ変換する。
/// 本文中の参照はリンクを外した後に「\*1」の形で残り、
/// 末尾の#note由来の定義は「\*1 本文」の行になっているので、
/// 定義行を「[^1]: 本文」へ、残った参照を「[^1]」へ書き換える。
/// 番号と本文の間の空白は行内のものだけを読み、
/// 定義を1行へ閉じ込めて後続の段落を巻き込まないようにする。
let private convertFootnotes (markdown: string) : string =
    let definition: Parse.TextParser<string> =
        Parse.lineStart >>. wikiFootnoteMark .>> Parse.inlineSpaces1
        .>>. Parse.restOfLine
        |>> fun struct (number, text) -> $"[^%s{number}]: %s{Parse.trimInlineEnd text}"

    let reference: Parse.TextParser<string> =
        wikiFootnoteMark |>> fun number -> $"[^%s{number}]"

    markdown |> Parse.replace definition |> Parse.replace reference

/// 連続する空行を1つへ潰し、前後の空白を落として末尾を改行1つで終える。
/// 行の削除を伴う整形の共通の仕上げ。
let private normalizeBlankLines (markdown: string) : string =
    /// 改行の並び。3つ以上続いていれば空行1つ分の2つへ縮める。
    let newlines: Parse.TextParser<string> =
        many1Chars (pchar '\n') |>> fun run -> if 3 <= run.Length then "\n\n" else run

    (Parse.replace newlines markdown).Trim() + "\n"

/// 脚注の定義行を本文から除けて、定義行の並びと残りの本文に分ける。
/// 定義はページの末尾に置かれて最後の節へ紛れ込むため、
/// 節を落としたり中身の有無を見たりする前に除けておく。
let private splitFootnoteDefinitions (markdown: string) : string list * string =
    let definitions, body =
        markdown.Split '\n'
        |> Array.toList
        |> List.partition (Parse.startsWith footnoteDefinition)

    definitions, String.concat "\n" body

/// 除けておいた脚注の定義行を文書の末尾へ戻して、空行を整える。
let private appendFootnoteDefinitions (definitions: string list) (body: string) : string =
    normalizeBlankLines (body + "\n\n" + String.concat "\n" definitions)

/// 本文を節の木として書き換える。
let private editSections (edit: Markdown.Section -> Markdown.Section) (body: string) : string =
    body |> Markdown.parseSections |> edit |> Markdown.toMarkdown

/// 最初の見出しより前を切り落とす。
/// wikiruの記事は本文が最初の見出しから始まり、
/// それより前は関連ページへのナビゲーションや注意書きで、ナレッジには要らない。
/// 見出しが無い文書はそのまま返す。
let private dropPreamble (root: Markdown.Section) : Markdown.Section =
    if List.isEmpty root.Children then
        root
    else
        { root with Body = [] }

/// コメント欄の節を小見出しごと落とす。
/// コメントそのものは後から読み込まれるので取得したHTMLには残らないが、
/// 投稿のルールを畳んで置いているページがあり、
/// 見出しだけを消すとルールの本文が直前の節の続きとして残ってしまう。
let private dropCommentSections (root: Markdown.Section) : Markdown.Section =
    root
    |> Markdown.filterDescendants (fun section ->
        section.Heading <> Some "コメント" && section.Heading <> Some "コメントフォーム")

/// 中身を持たない節を落とす。
/// 節の中身が抽出や後始末で全て落ちると、見出しだけが残る。
/// 本文が空白だけで小見出しも残っていない節は、もう何も指していない。
/// 小見出しが全て落ちて空になった上位の節も落ちるように、深い方から先に見る。
let rec private dropEmptySections (section: Markdown.Section) : Markdown.Section =
    let hasContent (child: Markdown.Section) : bool =
        not (List.forall String.IsNullOrWhiteSpace child.Body)
        || not (List.isEmpty child.Children)

    { section with
        Children = section.Children |> List.map dropEmptySections |> List.filter hasContent }

/// 節の単位での後始末をして、空行を整える。
/// 脚注の定義は節の採否に関わらず全て残して、文書の末尾へ集める。
let private pruneSections (markdown: string) : string =
    let definitions, body = splitFootnoteDefinitions markdown

    body
    |> editSections (dropPreamble >> dropCommentSections >> dropEmptySections)
    |> appendFootnoteDefinitions definitions

/// 外部リンクの跡として残った🌐アイコンを消す。
/// wikiのJavaScriptが外部リンクの中へ付け足すアイコンは、
/// リンク外しの後にただの文字として残る。
let private removeExternalLinkIcon (markdown: string) : string =
    Parse.remove (Parse.inlineSpaces >>. pstring "🌐") markdown

/// 読点とカンマの前に残った空白を詰める。
/// セル内改行の結合で挟んだ読点の前に、元の空白テキストノード由来の空白が残ることがある。
/// 表を箇条書きにした項目の区切りのカンマの前にも、強調の中の末尾の空白が残ることがある。
/// 日本語では読点の前に空白を置かない。
let private trimSpaceBeforeComma (markdown: string) : string =
    Parse.remove (Parse.inlineSpaces1 .>> lookAhead (anyOf "、,")) markdown

/// 区切り文字だけになった行を消す。
/// リンクを「/」で並べたナビゲーションは、並べた要素が落ちると区切り文字だけの行になる。
/// セル内改行の結合で挟んだ読点も、続く要素が空行や空列の除去で消えると孤立する。
/// 空白は行内のものだけに限定して、行を跨いだ巻き込みを防ぐ。
let private removeSeparatorRemnant (markdown: string) : string =
    Parse.remove
        (Parse.lineStart
         >>. Parse.inlineSpaces
         >>. skipMany1 (anyOf "/、" >>. Parse.inlineSpaces)
         .>> Parse.lineEnd)
        markdown

/// 編集者向けの欄の案内を消す。
/// ページのテンプレートが置いていく「〜を書く欄です。」は、
/// 記事を書く人へ向けた欄の案内で、生徒についての事実ではない。
/// 行全体が案内であることを求めるため、手前に別の文を持つ行は残す。
/// 案内より前は句点も改行も含まない並びに限って、手前の文や行を巻き込まないようにする。
let private removeEditorNotice (markdown: string) : string =
    /// 案内の文の中の1文字。句点で文が、改行で行が切れる。
    let withinSentence: Parse.TextParser<char> =
        satisfy (fun c -> c <> '。' && c <> '\r' && c <> '\n')

    Parse.remove
        (Parse.lineStart >>. manyCharsTill withinSentence (pstring "を書く欄です。")
         .>> Parse.inlineSpaces
         .>> Parse.lineEnd)
        markdown

/// 立ち絵に添えられた「クリックで全体画像」の案内を消す。
/// 元のページでは縮小画像を包む全体画像へのリンクの説明で、
/// リンクを外した後は辿る先の無い案内だけが画像の後ろに残る。
/// セル内改行の結合で画像との間に挟まった読点ごと消す。
let private removeFullImageCaption (markdown: string) : string =
    markdown.Replace("、クリックで全体画像", "").Replace("クリックで全体画像", "")

/// 空行と同じ意味しか持たない行を消す。
/// 実体参照のままの&nbsp;と、中身の無い引用が対象になる。
/// 残すと見出しが中身を持つかの判断で本文として数えられてしまう。
let private removeBlankLikeLine (markdown: string) : string =
    Parse.remove
        (Parse.lineStart >>. Parse.inlineSpaces >>. (pstring "&nbsp;" <|> pstring ">")
         .>> Parse.inlineSpaces
         .>> Parse.lineEnd)
        markdown

/// 変換後Markdownの後始末。
/// wikiruのページ由来のノイズを落として、ナレッジとして読める形へ整える。
/// 何をどの順で施すかは以下のパイプラインがそのまま示していて、
/// 個々の段が何を落とすのかはそれぞれの関数に書いてある。
/// 順序に意味があるのは以下の2箇所だけで、残りは互いに影響しない。
///
/// - 脚注の変換は節の単位での後始末より先。
///   節から脚注の定義を除けておく判定を、変換後の1つの表記だけで書けるようにするため
/// - 節の単位での後始末は最後。
///   中身を失った節は、行を落とす段を全て終えてからでないと取りこぼすため。
///   行を落とした跡の空行もここで潰す
let cleanupMarkdown (markdown: string) : string =
    markdown
    |> convertFootnotes
    |> removeExternalLinkIcon
    |> trimSpaceBeforeComma
    |> removeSeparatorRemnant
    |> removeEditorNotice
    |> removeFullImageCaption
    |> removeBlankLikeLine
    |> pruneSections

/// 節を分けている見出しの深さ。wikiruの記事はh2で節を分ける。
let private sectionLevel = 2

/// h2見出しのホワイトリストでMarkdownのセクションを選別する。
/// h2の見出しがtitlesに載っているセクションだけを、h3以下の小見出しごと残す。
/// 最初のh2より前の部分はセクションに属さないのでそのまま残す。
/// 脚注定義は元のセクション位置に関わらず、
/// 残った本文から参照されているものだけを文書末尾へ集める。
/// 選別で参照ごと落ちた定義を残すと宙に浮いた脚注になるため。
let filterSections (titles: string list) (markdown: string) : string =
    let definitions, body = splitFootnoteDefinitions markdown

    let filtered =
        body
        |> editSections (
            Markdown.filterDescendants (fun section ->
                section.Level <> sectionLevel
                || Option.exists (fun title -> List.contains title titles) section.Heading)
        )

    // 参照番号の集合を本文から一度だけ作り、定義側は集合参照で絞り込む。
    let referencedNumbers =
        Parse.findAll footnoteReference filtered |> List.map _.Value |> Set.ofList

    let referencedDefinitions =
        definitions
        |> List.filter (fun definition ->
            Parse.tryPrefix footnoteDefinition definition
            |> Option.exists (fun number -> Set.contains number referencedNumbers))

    appendFootnoteDefinitions referencedDefinitions filtered

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
        | Str.NullOrEmpty -> source.AbsoluteUri
        | Str.NonEmpty query -> Uri.UnescapeDataString query

    $"出典: [%s{pageName} - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{source.AbsoluteUri})\n\n"

/// sourceHeaderが組み立てる出典の行。リンクのURLを返す。
/// 生成したファイルから出典を読み戻す側が、書く側と同じ場所で形を知るためにここへ置く。
let sourceLine: Parse.TextParser<string> =
    pstring "出典: ["
    >>. skipMany1Satisfies (fun c -> c <> ']')
    >>. pstring "]("
    >>. many1Chars (satisfy (fun c -> c <> ')'))
    .>> pchar ')'

/// ページ名からナレッジファイル先頭に付ける出典の表記を組み立てる。
let knowledgeHeader (pageName: string) : string = sourceHeader (pageUri pageName)

/// 出力先の親ディレクトリを作ってからファイルへ書き出す。
let private writeFile (outputPath: string) (content: string) : Task<unit> =
    task {
        match Path.GetDirectoryName outputPath with
        | Str.NullOrEmpty -> ()
        | Str.NonEmpty directory -> Directory.CreateDirectory directory |> ignore

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
/// 生徒個別ページ向けの抽出設定のうち、用途に合ったものを受け取る。
let private fetchStudentSections
    (query: Extract.ContentQuery)
    (titles: string list)
    (pageName: string)
    : Task<string> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) query
        let! markdown = Pandoc.toMarkdown html
        return filterSections titles (cleanupMarkdown markdown)
    }

/// wikiruの生徒個別ページをナレッジ用Markdownへ変換する。
/// 事実を載せているセクションだけへ選別する。
let fetchStudentMarkdown (pageName: string) : Task<string> =
    fetchStudentSections studentContentQuery studentSectionTitles pageName

/// wikiruのプレイアブルではない登場人物のページをナレッジ用Markdownへ変換する。
/// 生徒個別ページと同じ抽出設定で、事実を載せているセクションだけへ選別する。
let fetchUnplayableMarkdown (pageName: string) : Task<string> =
    fetchStudentSections studentContentQuery unplayableSectionTitles pageName

/// 基本情報セクションの「入手方法」の見出し以降(入手方法・ステータス・装備)を切り落とす。
/// role-playの参照に要るのはプロフィールと紹介文だけで、
/// 性能データはrole-playには使わないため。
/// 「入手方法」は見出しプラグイン由来の太字行で、h3以下の見出しにはならない。
let trimGameplayDetails (markdown: string) : string =
    /// 切り落とす範囲の終わり。次のh2見出しの行頭か文書の末尾で、入力は読み進めない。
    let sectionEnd: Parse.TextParser<unit> =
        lookAhead (Parse.lineStart >>. pstring "## " >>% ()) <|> eof

    normalizeBlankLines (
        Parse.remove
            (Parse.lineStart >>. pstring "**入手方法**" >>. skipManyTill anyChar sectionEnd)
            markdown
    )

/// wikiruの生徒個別ページをrole-playスキルの参照用Markdownへ変換する。
/// プロフィールと紹介文とボイスと小ネタだけを残す。
let fetchRolePlayMarkdown (pageName: string) : Task<string> =
    task {
        let! markdown = fetchStudentSections rolePlayContentQuery rolePlaySectionTitles pageName
        return trimGameplayDetails markdown
    }

/// ナレッジ本体に節が1つも無かった時のページ名。
exception StudentSectionNotFound of pageName: string

/// プロフィールに「フルネーム」の項目が無かった時のページ名。
exception StudentFullNameNotFound of pageName: string

/// フルネームがページ名と名前で繋がらなかった時のページ名とフルネーム。
exception StudentFullNameMismatch of pageName: string * fullName: string

/// プロフィールの箇条書きの「フルネーム」の項目。キーに続く値を返す。
/// 空欄のセルは項目に残らないため、値の無い「- フルネーム」だけの行も空として読む。
let private fullNameItem: Parse.TextParser<string> =
    let spaces: Parse.TextParser<unit> = skipMany (pchar ' ')

    let value: Parse.TextParser<string> =
        pchar ':' >>. spaces >>. Parse.restOfLine |>> _.TrimEnd(' ')

    Parse.lineStart >>. pstring "- フルネーム" >>. (value <|>% "")
    .>> spaces
    .>> Parse.lineEnd

/// フルネームに添えられたふりがなの括弧。
let private ruby: Parse.TextParser<char> =
    pchar '(' >>. skipManySatisfies (fun c -> c <> ')') >>. pchar ')'

/// プロフィールのフルネームからふりがなを外したもの。
/// 項目が無いか、空欄で空白しか残らない時はNone。
let private tryFullName (markdown: string) : string option =
    Parse.tryFind fullNameItem markdown
    |> Option.bind (fun found ->
        match (Parse.remove ruby found.Value).Trim() with
        | Str.NullOrEmpty -> None
        | Str.NonEmpty fullName -> Some fullName)

/// フルネームのうちページ名の先頭と重なる部分より前を姓として、ページ名の前へ置く。
/// 重なる部分が無ければ、別人の名前を作らないように止まる。
let private prependFamilyName (pageName: string) (fullName: string) : string =
    let overlap =
        seq { 0 .. fullName.Length - 1 }
        |> Seq.tryFind (fun index ->
            pageName.StartsWith(fullName.Substring index, StringComparison.Ordinal))

    match overlap with
    | Some index -> fullName.Substring(0, index) + pageName
    | None -> raise (StudentFullNameMismatch(pageName, fullName))

/// ページ名の前へ姓を補う。
/// wikiruのページ名は「アイリ」「アイリ（バンド）」のように姓を持たないが、
/// スキルの説明は本文を読み込む前の一覧としてしか読まれないため、
/// 姓が無いとそこだけでは誰のことなのかが決まらない。
/// プロフィールの項目のフルネームからふりがなを外し、
/// ページ名の先頭と重なる部分より前を姓としてページ名の前へ置く。
/// 衣装の接尾辞はページ名の側に残るので「アイリ（バンド）」は「栗村アイリ（バンド）」になり、
/// 姓を持たない「ケイ」ではフルネーム全体が重なって何も足さない。
let familyNamedPageName (pageName: string) (markdown: string) : string =
    match tryFullName markdown with
    | Some fullName -> prependFamilyName pageName fullName
    | None -> raise (StudentFullNameNotFound pageName)

/// プレイアブルではない登場人物のページ名の前へ、分かっていれば姓を補う。
/// 作中で姓が明かされていない人物や、アロナのように生徒ではない人物がいて、
/// フルネームの項目が無いか空欄のページがあるため、その時はページ名をそのまま使う。
/// フルネームがあるのにページ名と繋がらない時は、生徒と同じく別人の名前を作らないように止まる。
let unplayableDisplayName (pageName: string) (markdown: string) : string =
    match tryFullName markdown with
    | Some fullName -> prependFamilyName pageName fullName
    | None -> pageName

/// ナレッジ本体に実際に含まれる節のh2の見出しを、現れる順に並べる。
/// 節を分けているのはh2なので、h3以下の小見出しは含めない。
/// studentSectionTitlesのホワイトリストをそのまま書くと、
/// 愛用品の節を持たないコトリ（応援団）のようなページで、
/// 存在しない節を存在すると宣言してしまう。
/// 読む側はその節を探し続けるか、抽出が壊れていると受け取ることになる。
/// filterSectionsは同じ見出しが2回現れてもどちらも残すが、
/// これは何が載っているかを述べるための一覧なので、重複は現れる順を保ったまま潰す。
let sectionTitles (markdown: string) : string list =
    let rec titles (section: Markdown.Section) : string list =
        (if section.Level = sectionLevel then
             Option.toList section.Heading
         else
             [])
        @ List.collect titles section.Children

    markdown |> Markdown.parseSections |> titles |> List.distinct

/// 生徒個別のスキルとプレイアブルではない登場人物のスキルのSKILL.md全体を組み立てる。
/// 1人分のナレッジは別ファイルへ分けるほどの量にならないため、
/// フロントマターと使い方の説明とナレッジ本体を1つのSKILL.mdに収める。
/// 事実を引くための参照専用で、ユーザがスラッシュコマンドとして呼ぶ意味が無いため、
/// user-invocable: falseでコマンドの一覧からは外す。
/// 両者で違うのは、説明と本文での呼び方と、説明に載せる名前の決め方だけ。
/// 名前の決め方はプロフィールを読むため、節の有無を先に確かめてから呼ぶように関数で受け取る。
let private characterSkillMarkdown
    (englishNoun: string)
    (japaneseNoun: string)
    (displayName: string -> string -> string)
    (skillName: string)
    (pageName: string)
    (markdown: string)
    : string =
    // 節を1つも読み取れないまま組み立てると、
    // 何が載っているかを述べない壊れた説明のスキルを書き出すことになる。
    let sections =
        match sectionTitles markdown with
        | [] -> raise (StudentSectionNotFound pageName)
        | titles -> String.concat "・" titles

    // スキルの説明は本文を読み込まなくても一覧としてモデルのコンテキストへ載るため、
    // 300人分が積み上がる長さにはせず、誰の事実を引けるかだけを述べる。
    // 何が載っているかは下の「データの構造」が節の一覧として持っている。
    let description =
        $"Facts about Blue Archive %s{englishNoun} %s{displayName pageName markdown}."

    $"""---
name: %s{skillName}
description: %s{description}
user-invocable: false
---

『ブルーアーカイブ』の%s{japaneseNoun}「%s{pageName}」のゲーム内外の事実を調べるためのスキルです。

# データの構造

- セクションは%s{sections}です
- 表は「列名: 値」の箇条書きに、表の題名はその下の見出しになっています
- 別バージョン(衣装違い)の生徒は別のページなので、このデータには含まれません

# 使う時の注意

- データに無い情報は、別バージョンの生徒のスキルや出典のページで確認してください。似た名前の生徒の性能を混ぜないでください
- 小ネタはゲーム内外の事実のまとめです。一方、wiki執筆者による解説や運用考察は著作権方針により含めていません。必要な場合は出典のページを直接参照してください

# ナレッジ

%s{knowledgeHeader pageName}%s{markdown}"""

/// 生徒スキルのSKILL.md全体を組み立てる。説明には姓を補った名前を載せる。
let studentSkillMarkdown (skillName: string) (pageName: string) (markdown: string) : string =
    characterSkillMarkdown "student" "生徒" familyNamedPageName skillName pageName markdown

/// プレイアブルではない登場人物のスキルのSKILL.md全体を組み立てる。
/// ロールプレイの対象にせず、生徒の会話で話題に上った時に設定を引くためのナレッジだけを持つ。
/// 生徒個別ページと同じ形のページなので、本文の組み立ては生徒と共有する。
/// 未実装の生徒も生徒なので、wikiruのレアリティの表記のNPCとは呼ばない。
/// アロナのように生徒ではない人物も含むため、説明と本文ではcharacterと登場人物と呼ぶ。
let unplayableSkillMarkdown (skillName: string) (pageName: string) (markdown: string) : string =
    characterSkillMarkdown "character" "登場人物" unplayableDisplayName skillName pageName markdown

/// 生徒個別ページと同じ形のページをMarkdown化し、組み立てたSKILL.mdとして書き出す。
/// スキル名は出力先のディレクトリ名と一致する必要があるため、出力パスから導出する。
/// 整形は掛けず、書き出したパスを返す。
let private writeCharacterSkill
    (fetch: string -> Task<string>)
    (build: string -> string -> string -> string)
    (pageName: string)
    (outputPath: string)
    : Task<string list> =
    task {
        let skillName =
            match Path.GetDirectoryName outputPath with
            | Str.NullOrEmpty ->
                raise (
                    ArgumentException($"スキル名を導出するディレクトリがありません: %s{outputPath}", nameof outputPath)
                )
            | Str.NonEmpty directory -> Path.GetFileName directory

        let! markdown = fetch pageName
        do! writeFile outputPath (build skillName pageName markdown)
        return [ outputPath ]
    }

/// wikiruの生徒個別ページをMarkdown化し、SKILL.mdとして書き出す。
let writeStudentSkill (pageName: string) (outputPath: string) : Task<string list> =
    writeCharacterSkill fetchStudentMarkdown studentSkillMarkdown pageName outputPath

/// wikiruのプレイアブルではない登場人物のページをMarkdown化し、SKILL.mdとして書き出す。
let writeUnplayableSkill (pageName: string) (outputPath: string) : Task<string list> =
    writeCharacterSkill fetchUnplayableMarkdown unplayableSkillMarkdown pageName outputPath

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

/// wikiruの学校別キャラクター一覧ページを取得して、所属のレコードの列へパースする。
let fetchSchoolEntries (pageName: string) : Task<School.Entry list> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) structuredContentQuery
        return School.parseHtml html
    }

/// wikiruのキャラクター一覧ページを取得して、並んでいる生徒個別ページのページ名を返す。
let fetchCharacterListPages (pageName: string) : Task<string list> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) structuredContentQuery
        return CharacterList.parseHtml html
    }

/// wikiruの学校別キャラクター一覧ページを構造化データへパースし、
/// LLM参照用のreference.mdと機械読み出し用のJSONを書き出す。
/// 整形は掛けず、書き出したパスを返す。
let writeSchool (pageName: string) (markdownPath: string) (jsonPath: string) : Task<string list> =
    task {
        let! entries = fetchSchoolEntries pageName
        do! writeFile markdownPath (knowledgeHeader pageName + School.toReferenceMarkdown entries)

        let document: School.Document =
            { Source = (pageUri pageName).AbsoluteUri
              Entries = entries }

        do! writeFile jsonPath (School.toJson document)
        return [ markdownPath; jsonPath ]
    }
