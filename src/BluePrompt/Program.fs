module BluePrompt.Program

let private usage =
    """使い方:
  BluePrompt wikiru-knowledge <ページ名> <出力ファイル>
    wikiruの記事をMarkdown化してナレッジファイルとして書き出す。
  BluePrompt wikiru-student-skill <ページ名> <SKILL.mdの出力パス>
    wikiruの生徒個別ページから事実セクションを抜き出し、スキル定義ごとSKILL.mdとして書き出す。
    スキル名は出力先のディレクトリ名から導出する。
  BluePrompt wikiru-html <ページ名> <出力ファイル>
    wikiruの記事から抽出した本文をMarkdown化せずHTMLのまま書き出す。"""

[<EntryPoint>]
let main argv =
    match argv with
    | [| "wikiru-knowledge"; pageName; outputPath |] ->
        let work =
            Browser.withBrowser (fun browser -> Wikiru.writeKnowledge browser pageName outputPath)

        work.GetAwaiter().GetResult()
        0
    | [| "wikiru-student-skill"; pageName; outputPath |] ->
        let work =
            Browser.withBrowser (fun browser ->
                Wikiru.writeStudentSkill browser pageName outputPath)

        work.GetAwaiter().GetResult()
        0
    | [| "wikiru-html"; pageName; outputPath |] ->
        let work =
            Browser.withBrowser (fun browser -> Wikiru.writeContentHtml browser pageName outputPath)

        work.GetAwaiter().GetResult()
        0
    | [||] ->
        printfn $"%s{usage}"
        0
    | _ ->
        eprintfn $"%s{usage}"
        1
