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
          // includeプラグインが差し込むinclude元ページへのリンク。
          // リンクアンラップの後にページ名だけの行が残り、直前の表の一部と誤読される。
          ".permalink"
          // 画像。lazyload用プレースホルダのdata URIしか取れずノイズになる。
          imageSelector
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
let studentContentQuery: Extract.ContentQuery =
    { contentQuery with
        RemoveSelectors =
            [ ".rgn-button"; ".rgn-description" ]
            @ List.filter
                (fun selector -> selector <> collapsibleSelector && selector <> imageSelector)
                contentQuery.RemoveSelectors
        ReplaceImagesWithAlt = true }

/// キャラ呼称表ページ用の抽出設定。
/// Appellation.parseが読む#bodyと#noteだけを残し、
/// contentQueryと同じ理由でスクリプト片・広告タグ・コメント欄と投稿フォーム由来の、
/// 任意ユーザー入力がレコードへ紛れ込む余地を断つ。
/// 一方でパースはDOMの構造をそのまま読むため、Markdown化前提の変形は掛けない。
/// リンクは脚注アンカー(note_super)のhref/titleと編集リンクの判定に必要なので外さず、
/// rowspanもparseTableが自前で扱うのでテーブルは平坦化しない。
/// セル内のアイコン画像はTextContentが空で無害なので、altへの置き換えもしない。
let appellationContentQuery: Extract.ContentQuery =
    { ContentSelectors = [ "#body"; "#note" ]
      RemoveSelectors =
        [ ".sticky-ads"
          "ins.adsbygoogle"
          ".pcomment"
          "#pcomment-form"
          "div[class*='pcmt-']"
          "script"
          "style"
          "noscript"
          "template" ]
      UnwrapLinks = false
      ReplaceImagesWithAlt = false
      FlattenTables = false }

/// 生徒個別ページからナレッジとして残すセクションの見出し。
/// ゲーム内の事実を載せているセクションだけを列挙するホワイトリスト。
/// 「ゲームにおいて」「運用考察」「小ネタ」などのwiki独自の解説・考察は
/// 著作権方針(plugins/jp-wikiru-bluearchive/README.md)によりそのままの形では置かないため、
/// 列挙されていないセクションは落ちる。
/// 「スキル成長素材」と「贈り物」は素材や品物が画像で表現されていて、
/// 画像除去後は数量だけが残り事実として読めないため外している。
let studentSectionTitles: string list =
    [ "基本情報"; "スキル"; "固有武器"; "愛用品"; "能力解放"; "絆ランクボーナス"; "絆ストーリー"; "ボイス" ]

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

/// 変換後Markdownの後始末。
/// 最初の見出しより前のナビゲーションを切り落とし、
/// 中身を取り除いて残骸になったコメント欄の見出しを消し、
/// 外部リンクの跡として残った🌐アイコンを消し、
/// セル内改行の結合で挟んだ読点の前に残った空白を詰め、
/// 画像だけのリンク列の跡や孤立した読点として残った区切り文字だけの行を消し、
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
    // セル内改行の結合で挟んだ読点も、続く要素が空行や空列の除去で消えると孤立する。
    // 空白は行内のものだけに限定して、行を跨いだ巻き込みを防ぐ。
    let withoutSeparatorRemnant =
        Regex.Replace(
            withoutSpaceBeforeComma,
            @"^[^\S\r\n]*([/、][^\S\r\n]*)+$",
            "",
            RegexOptions.Multiline
        )

    normalizeBlankLines (convertFootnotes withoutSeparatorRemnant)

/// GFMの脚注定義行(「[^1]: 本文」)への一致。
let private footnoteDefinitionPattern = Regex @"^\[\^(\d+)\]: "

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
let writeKnowledge (pageName: string) (outputPath: string) : Task<unit> =
    task {
        let! markdown = fetchMarkdown pageName
        do! writeFile outputPath (knowledgeHeader pageName + markdown)
        do! Fmt.formatFile outputPath
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
let writeStudentSkill (pageName: string) (outputPath: string) : Task<unit> =
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
        do! Fmt.formatFile outputPath
    }

/// wikiruの生徒個別ページをMarkdown化し、
/// role-playスキルが参照する衣装別の参照ファイルとして書き出す。
/// 書き出した直後にnix fmtを掛けて、生成コマンドだけで内容が確定するようにする。
let writeRolePlayReference (pageName: string) (outputPath: string) : Task<unit> =
    task {
        let! markdown = fetchRolePlayMarkdown pageName
        do! writeFile outputPath (knowledgeHeader pageName + markdown)
        do! Fmt.formatFile outputPath
    }

/// wikiruのキャラ呼称表ページを構造化データへパースし、
/// LLM参照用のreference.mdと機械読み出し用のJSONを一度の取得から書き出す。
/// JSONをリポジトリへ併置することで、
/// 後段の生成処理がwikiruへ再アクセスせずに呼称を読み出せるようにする。
/// どちらも書き出した直後にnix fmtを掛けて、生成コマンドだけで内容が確定するようにする。
let writeAppellation (pageName: string) (markdownPath: string) (jsonPath: string) : Task<unit> =
    task {
        let! html = Page.fetchContentHtml (pageUri pageName) appellationContentQuery
        let entries = Appellation.parseHtml html

        do!
            writeFile
                markdownPath
                (knowledgeHeader pageName + Appellation.toReferenceMarkdown entries)

        do! Fmt.formatFile markdownPath

        let document: Appellation.Document =
            { Source = (pageUri pageName).AbsoluteUri
              Entries = entries }

        do! writeFile jsonPath (Appellation.toJson document)
        do! Fmt.formatFile jsonPath
    }

/// role-playスキルのテンプレート内で呼称表を差し込む位置を示すプレースホルダ。
let appellationPlaceholder: string = "{{appellation}}"

/// テンプレートに呼称表のプレースホルダが見つからなかった時のファイルパス。
exception AppellationPlaceholderNotFound of path: string

/// role-playスキルのテンプレートのプレースホルダへ呼称表を流し込んでSKILL.mdの内容を組み立てる。
/// プレースホルダが無いテンプレートは呼称表が黙って落ちるため、Noneを返して失敗にする。
let renderRolePlaySkill (appellation: string) (template: string) : string option =
    if template.Contains appellationPlaceholder then
        Some(template.Replace(appellationPlaceholder, appellation))
    else
        None

/// 手書きのテンプレートと生成済みのappellation.jsonから、
/// role-playスキルのSKILL.md全体を生成する。
/// 没入感を左右する呼称は別ファイルへ分けず、スキル本体へ直接埋め込む。
/// テンプレートは出力先と同じディレクトリのSKILL.template.mdから読む。
/// wikiruへはアクセスせず、リポジトリへ併置したJSONだけで完結する。
/// 出典の表記はJSONに記録された出典URLからページ名を復元して組み立てる。
/// 書き出した直後にnix fmtを掛けて、生成コマンドだけで内容が確定するようにする。
let writeRolePlaySkill (caller: string) (jsonPath: string) (outputPath: string) : Task<unit> =
    task {
        let templatePath =
            Path.Combine(
                (match Path.GetDirectoryName outputPath with
                 | null -> ""
                 | directory -> directory),
                "SKILL.template.md"
            )

        let! template = File.ReadAllTextAsync templatePath
        let! json = File.ReadAllTextAsync jsonPath
        let document = Appellation.ofJson json
        let pageName = Uri.UnescapeDataString((Uri document.Source).Query.TrimStart '?')

        let appellation =
            knowledgeHeader pageName + Appellation.toCallerMarkdown caller document.Entries

        match renderRolePlaySkill appellation template with
        | None -> raise (AppellationPlaceholderNotFound templatePath)
        | Some skill ->
            do! writeFile outputPath skill
            do! Fmt.formatFile outputPath
    }
