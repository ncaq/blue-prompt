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
  BluePrompt roleplay-skill <キャラクター名> <appellation.jsonのパス> <SKILL.mdの出力パス>
    出力先と同じディレクトリの手書きテンプレートSKILL.template.mdのプレースホルダへ、
    生成済みのappellation.jsonから抜き出した指定キャラクターの呼称表を流し込み、
    role-playスキルのSKILL.md全体を生成する。wikiruへはアクセスしない。
  BluePrompt open-webui-model <スキルディレクトリ> <出力ファイル>
    スキルのSKILL.mdとリンクされた参照ファイルをインライン化して、
    システムプロンプトへ焼き込んだOpen WebUIのModelFormのJSONを書き出す。
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
    | [| "roleplay-skill"; caller; jsonPath; outputPath |] ->
        (Wikiru.writeRolePlaySkill caller jsonPath outputPath).GetAwaiter().GetResult()
        0
    | [| "open-webui-model"; skillDirectory; outputPath |] ->
        (OpenWebui.writeModel skillDirectory outputPath).GetAwaiter().GetResult()
        0
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
