/// ブルーアーカイブ攻略有志Wiki(bluearchive.wikiru.jp)固有のページ取得とナレッジ化。
module BluePrompt.Wikiru

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Playwright
open System.Threading.Tasks

/// wikiruのページ名から記事URLを組み立てる。
let pageUri (pageName: string) : Uri =
    Uri $"https://bluearchive.wikiru.jp/?%s{Uri.EscapeDataString pageName}"

/// wikiruの記事からナレッジとして使う部分を抜き出す設定。
/// 本文(#body)と脚注(#note)を残し、サイトのヘッダ・サイドバー・フッタは含めない。
let contentQuery: Page.ContentQuery =
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
          // 折りたたみに本文が入っているページを扱うことになったら見直す。
          ".rgn-container"
          // 広告。
          ".sticky-ads"
          "ins.adsbygoogle"
          // コメント欄と投稿フォームとその部品(絵文字ピッカーなど)。
          // wiki独自コンテンツは扱わない方針のため。
          ".pcomment"
          "#pcomment-form"
          "div[class*='pcmt-']"
          // 表内などに埋め込まれた他ページの編集リンク。
          // リンクアンラップの後に「EDIT」という文字列だけが残ってノイズになる。
          "a[href*='cmd=edit']"
          // 画像。lazyload用プレースホルダのdata URIしか取れずノイズになる。
          "img"
          // 表示対象ではない要素。pandocはタグを落としてもテキスト内容を本文へ混ぜることがあり、
          // wikiのプラグインや広告タグ由来のスクリプト片がナレッジへ紛れ込む余地がある。
          "script"
          "style"
          "noscript"
          "template" ]
      UnwrapLinks = true
      ReplaceImagesWithAlt = false
      FlattenTables = true }

/// 生徒個別ページ用の抽出設定。
/// 生徒ページでは絆ランクボーナスや絆ストーリー一覧やボイスといった本文が
/// 折りたたみ(rgn)の中に入っているため、折りたたみ全体は除去せず、
/// 開閉ボタンと「クリックで展開」のラベルだけを取り除いて中身を残す。
/// スキル成長素材のような表は素材を画像で表現しているため、
/// 画像は除去せずaltに入っている素材名のテキストへ置き換える。
let studentContentQuery: Page.ContentQuery =
    { contentQuery with
        RemoveSelectors =
            [ ".rgn-button"; ".rgn-description" ]
            @ List.filter
                (fun selector -> selector <> ".rgn-container" && selector <> "img")
                contentQuery.RemoveSelectors
        ReplaceImagesWithAlt = true }

/// 生徒個別ページからナレッジとして残すセクションの見出し。
/// ゲーム内の事実を載せているセクションだけを列挙するホワイトリスト。
/// 「ゲームにおいて」「運用考察」「小ネタ」などのwiki独自の解説・考察は
/// 著作権方針(plugins/jp-wikiru-bluearchive/README.md)によりそのままの形では置かないため、
/// 列挙されていないセクションは落ちる。
/// 「スキル成長素材」と「贈り物」は素材や品物が画像で表現されていて、
/// 画像除去後は数量だけが残り事実として読めないため外している。
let studentSectionTitles: string list =
    [ "基本情報"; "スキル"; "固有武器"; "愛用品"; "能力解放"; "絆ランクボーナス"; "絆ストーリー"; "ボイス" ]

/// 最初の見出しより前を切り落とす。
/// wikiruの記事は本文が最初の見出しから始まり、
/// それより前は関連ページへのナビゲーションや注意書きで、ナレッジには要らない。
/// 見出しが無いMarkdownはそのまま返す。
let private trimPreamble (markdown: string) : string =
    match Regex.Match(markdown, @"^#{1,6} ", RegexOptions.Multiline) with
    | m when m.Success -> markdown[m.Index ..]
    | _ -> markdown

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

/// 変換後Markdownの後始末。
/// 最初の見出しより前のナビゲーションを切り落とし、
/// 中身を取り除いて残骸になったコメント欄の見出しを消し、
/// 外部リンクの跡として残った🌐アイコンを消し、
/// 画像だけのリンク列の跡として残った区切り文字だけの行を消し、
/// 脚注をGFMの文法へ変換し、連続する空行を1つへ潰す。
let cleanupMarkdown (markdown: string) : string =
    let withoutCommentHeading =
        Regex.Replace(trimPreamble markdown, @"^#{1,6} コメント(フォーム)?\s*$", "", RegexOptions.Multiline)

    // wikiのJavaScriptが外部リンクの中へ付け足す🌐アイコンは、
    // リンク外しの後にただの文字として残る。
    let withoutExternalLinkIcon =
        Regex.Replace(withoutCommentHeading, @"[^\S\r\n]*🌐", "")

    // セル内改行の結合で挟んだ読点の前に、元の空白テキストノード由来の空白が残ることがある。
    // 日本語では読点の前に空白は置かないので詰める。
    let withoutSpaceBeforeComma =
        Regex.Replace(withoutExternalLinkIcon, @"[^\S\r\n]+(?=、)", "")

    // 画像だけのリンクを「/」で並べたナビゲーションは、
    // 画像除去とリンク外しの後に区切り文字だけの行になる。
    // 空白は行内のものだけに限定して、行を跨いだ巻き込みを防ぐ。
    let withoutSeparatorRemnant =
        Regex.Replace(
            withoutSpaceBeforeComma,
            @"^[^\S\r\n]*(/[^\S\r\n]*)+$",
            "",
            RegexOptions.Multiline
        )

    let collapsed =
        Regex.Replace(convertFootnotes withoutSeparatorRemnant, @"\n{3,}", "\n\n")

    collapsed.Trim() + "\n"

/// GFMの脚注定義行(「[^1]: 本文」)への一致。
let private footnoteDefinitionPattern = Regex(@"^\[\^(\d+)\]: ")

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

    let filtered =
        bodyLines
        |> Array.fold
            (fun (kept, keeping) line ->
                match Regex.Match(line, @"^## +(.*?)\s*$") with
                | m when m.Success ->
                    let keeping = List.contains m.Groups[1].Value titles
                    ((if keeping then line :: kept else kept), keeping)
                | _ -> ((if keeping then line :: kept else kept), keeping))
            ([], true)
        |> fst
        |> List.rev

    let body = String.concat "\n" filtered

    let referencedDefinitions =
        definitions
        |> Array.filter (fun definition ->
            let number = footnoteDefinitionPattern.Match(definition).Groups[1].Value
            body.Contains $"[^%s{number}]")

    let withDefinitions =
        if Array.isEmpty referencedDefinitions then
            body
        else
            body.TrimEnd() + "\n\n" + String.concat "\n" referencedDefinitions

    let collapsed = Regex.Replace(withDefinitions, @"\n{3,}", "\n\n")
    collapsed.Trim() + "\n"

/// wikiruの記事ページをナレッジ用Markdownへ変換する。
/// pandocArgumentsでpandocの変換オプションを調整できる。
let fetchMarkdownWith
    (browser: IBrowser)
    (pandocArguments: string list)
    (pageName: string)
    : Task<string> =
    task {
        let! html = Page.fetchContentHtml browser (pageUri pageName) contentQuery
        let! markdown = Pandoc.toMarkdownWithArguments pandocArguments html
        return cleanupMarkdown markdown
    }

/// wikiruの記事ページを既定のpandoc引数でナレッジ用Markdownへ変換する。
let fetchMarkdown (browser: IBrowser) (pageName: string) : Task<string> =
    fetchMarkdownWith browser Pandoc.defaultMarkdownArguments pageName

/// ナレッジファイル先頭に付ける出典の表記。
/// Uri.ToStringはパーセントエンコードを解いた表示用文字列を返すため、リンクにはAbsoluteUriを使う。
let knowledgeHeader (pageName: string) : string =
    $"出典: [%s{pageName} - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{(pageUri pageName).AbsoluteUri})\n\n"

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
/// 書き出した直後にnix fmtを掛けて、生成コマンドだけで内容が確定するようにする。
let writeKnowledge (browser: IBrowser) (pageName: string) (outputPath: string) : Task<unit> =
    task {
        let! markdown = fetchMarkdown browser pageName
        do! writeFile outputPath (knowledgeHeader pageName + markdown)
        do! Fmt.formatFile outputPath
    }

/// wikiruの記事から抽出・変形した本文を、Markdown化せずHTMLのまま書き出す。
/// 抽出設定を調整する時にpandoc変換前の中間HTMLを確認するための入口。
let writeContentHtml (browser: IBrowser) (pageName: string) (outputPath: string) : Task<unit> =
    task {
        let! html = Page.fetchContentHtml browser (pageUri pageName) contentQuery
        do! writeFile outputPath html
    }

/// wikiruの生徒個別ページをナレッジ用Markdownへ変換する。
/// 折りたたみの中身を残した設定で取得し、事実を載せているセクションだけへ選別する。
let fetchStudentMarkdown (browser: IBrowser) (pageName: string) : Task<string> =
    task {
        let! html = Page.fetchContentHtml browser (pageUri pageName) studentContentQuery
        let! markdown = Pandoc.toMarkdown html
        return filterSections studentSectionTitles (cleanupMarkdown markdown)
    }

/// 生徒スキルのSKILL.md全体を組み立てる。
/// 生徒1人分のナレッジは別ファイルへ分けるほどの量にならないため、
/// フロントマターと使い方の説明とナレッジ本体を1つのSKILL.mdに収める。
let studentSkillMarkdown (skillName: string) (pageName: string) (markdown: string) : string =
    // フロントマターのdescriptionは1行である必要があるため、ソース上でだけ分割して結合する。
    let description =
        String.concat
            " "
            [ $"Lookup facts about %s{pageName}, a Blue Archive student,"
              "such as profile, stats, skills, unique weapon, gear, bond stories and voice lines."
              $"Use when answering questions about %s{pageName},"
              "checking the in-game performance data,"
              $"or role-playing scenes that involve %s{pageName}." ]

    $"""---
name: %s{skillName}
description: %s{description}
---

『ブルーアーカイブ』の生徒「%s{pageName}」のゲーム内の事実を調べるためのスキルです。

ステータスやスキルの数値は捏造されやすい知識なので、
記憶で答えずに以下のデータを引いてください。

# データの構造

- セクションは基本情報・スキル・固有武器・愛用品・能力解放・絆ランクボーナス・絆ストーリー・ボイスです
- 縦に結合されたセルは各行に同じ内容が複製され、横に結合されたセルの残りは空になっています
- 別バージョン(衣装違い)の生徒は別のページなので、このデータには含まれません

# 使う時の注意

- データに無い情報は勝手に補完せず「確認できていない」として扱ってください。似た名前の生徒の性能を混ぜないでください
- wiki執筆者による解説や運用考察は著作権方針により含めていません。必要な場合は出典のページを直接参照してください

# ナレッジ

%s{knowledgeHeader pageName}%s{markdown}"""

/// wikiruの生徒個別ページをMarkdown化し、SKILL.mdとして書き出す。
/// スキル名は出力先のディレクトリ名と一致する必要があるため、出力パスから導出する。
/// 書き出した直後にnix fmtを掛けて、生成コマンドだけで内容が確定するようにする。
let writeStudentSkill (browser: IBrowser) (pageName: string) (outputPath: string) : Task<unit> =
    task {
        let skillName =
            match Path.GetDirectoryName outputPath with
            | null
            | "" ->
                raise (
                    ArgumentException($"スキル名を導出するディレクトリがありません: %s{outputPath}", nameof outputPath)
                )
            | directory -> Path.GetFileName directory

        let! markdown = fetchStudentMarkdown browser pageName
        do! writeFile outputPath (studentSkillMarkdown skillName pageName markdown)
        do! Fmt.formatFile outputPath
    }
