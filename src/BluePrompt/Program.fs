module BluePrompt.Program

let private usage =
    """使い方:
  BluePrompt wikiru-knowledge <ページ名> <出力ファイル>
    wikiruの記事をMarkdown化してナレッジファイルとして書き出す。
  BluePrompt wikiru-student-skill <ページ名> <SKILL.mdの出力パス>
    wikiruの生徒個別ページから事実セクションを抜き出し、スキル定義ごとSKILL.mdとして書き出す。
    スキル名は出力先のディレクトリ名から導出する。
  BluePrompt wikiru-roleplay-reference <ページ名> <出力ファイル>
    wikiruの生徒個別ページからプロフィールとボイスを抜き出し、
    role-playスキルが参照する衣装別の参照ファイルとして書き出す。
  BluePrompt wikiru-appellation <ページ名> <reference.mdの出力> <JSONの出力>
    wikiruのキャラ呼称表を構造化し、
    LLM参照用のreference.mdと機械読み出し用のJSONを書き出す。
  BluePrompt wikiru-school <ページ名> <出力ファイル>
    wikiruの学校別キャラクター一覧を構造化し、
    学校ごとの一覧のreference.mdを書き出す。
  BluePrompt roleplay-skill <キャラクター名> <共通テンプレートのパス>
      <appellation.jsonのパス> <SKILL.mdまたはMODEL.mdの出力パス>
    全生徒で共通のテンプレートのプレースホルダへ、
    出力先と同じディレクトリのcharacter.mdの手書きの部分と衣装別の参照ファイルの一覧、
    生成済みのappellation.jsonから抜き出した指定キャラクターの呼称表を流し込み、
    role-playスキルの本文全体を生成する。wikiruへはアクセスしない。
    Claude Code向けのSKILL.mdとOpen WebUIのModel向けのMODEL.mdの2つの届け先があり、
    どちらを書き出すかは渡すテンプレートと出力先が決める。
  BluePrompt open-webui-model <スキルディレクトリ> <出力ファイル>
    スキルのMODEL.md(無ければSKILL.md)とリンクされた参照ファイルをインライン化して、
    システムプロンプトへ焼き込んだOpen WebUIのModelFormのJSONを書き出す。
  BluePrompt open-webui-knowledge <スキルディレクトリ> <出力ディレクトリ>
    スキルのSKILL.mdとリンクされたMarkdownの参照ファイルを見出しの単位へ分割して、
    Open WebUIのKnowledgeコレクションの定義一式を書き出す。
  BluePrompt open-webui-sync <モデル定義ディレクトリ> <ベースURL>
      [--base-model-id <id>] [--api-key-file <パス>]
      [--knowledge <ディレクトリ>] [--rag-template-file <パス>]
    open-webui-modelが生成したModelFormのJSON群をOpen WebUIのインスタンスへ同期する。
    無ければ作成し、差分があれば上書きし、差分が無ければ書き込まない。
    --knowledgeを与えるとopen-webui-knowledgeの生成物も同じ方針で同期し、
    Modelのmeta.knowledgeへコレクションのidを解決して紐付ける。
    --rag-template-fileを与えるとインスタンスのRAGプロンプトテンプレートも同期する。
  BluePrompt wikiru-html <ページ名> <出力ファイル>
    wikiruの記事から抽出した本文をMarkdown化せずHTMLのまま書き出す。
  BluePrompt wikiru-student-html <ページ名> <出力ファイル>
    wikiru-htmlの生徒個別ページ用。生徒個別ページの抽出設定で書き出す。"""

[<EntryPoint>]
let main argv =
    match argv with
    | [| "wikiru-knowledge"; pageName; outputPath |] ->
        (Wikiru.writeKnowledge pageName outputPath).GetAwaiter().GetResult()
        0
    | [| "wikiru-student-skill"; pageName; outputPath |] ->
        (Wikiru.writeStudentSkill pageName outputPath).GetAwaiter().GetResult()
        0
    | [| "wikiru-roleplay-reference"; pageName; outputPath |] ->
        (Wikiru.writeRolePlayReference pageName outputPath).GetAwaiter().GetResult()
        0
    | [| "wikiru-appellation"; pageName; markdownPath; jsonPath |] ->
        (Wikiru.writeAppellation pageName markdownPath jsonPath).GetAwaiter().GetResult()
        0
    | [| "wikiru-school"; pageName; outputPath |] ->
        (Wikiru.writeSchool pageName outputPath).GetAwaiter().GetResult()
        0
    | [| "roleplay-skill"; caller; templatePath; jsonPath; outputPath |] ->
        (RolePlay.writeSkill caller templatePath jsonPath outputPath).GetAwaiter().GetResult()

        0
    | [| "open-webui-model"; skillDirectory; outputPath |] ->
        (OpenWebui.writeModel skillDirectory outputPath).GetAwaiter().GetResult()
        0
    | [| "open-webui-knowledge"; skillDirectory; outputDirectory |] ->
        (OpenWebuiKnowledge.writeKnowledge skillDirectory outputDirectory).GetAwaiter().GetResult()

        0
    // 引数の個数の検証と説明はparseOptionsへ一本化する。
    | argv when 1 <= argv.Length && argv[0] = "open-webui-sync" ->
        try
            (OpenWebuiSync.sync (OpenWebuiSync.parseOptions (List.ofArray argv[1..])))
                .GetAwaiter()
                .GetResult()

            0
        with OpenWebuiSync.SyncError message ->
            // 引数や同期の失敗はスタックトレースではなく理由だけを表示する。
            eprintfn $"%s{message}"
            1
    | [| "wikiru-html"; pageName; outputPath |] ->
        (Wikiru.writeContentHtml Wikiru.contentQuery pageName outputPath).GetAwaiter().GetResult()

        0
    | [| "wikiru-student-html"; pageName; outputPath |] ->
        (Wikiru.writeContentHtml Wikiru.studentContentQuery pageName outputPath)
            .GetAwaiter()
            .GetResult()

        0
    | [||] ->
        printfn $"%s{usage}"
        0
    | _ ->
        eprintfn $"%s{usage}"
        1
