module BluePrompt.Program

let private usage =
    """使い方:
  BluePrompt wikiru-knowledge <ページ名> <出力ファイル>
    wikiruの記事をMarkdown化してナレッジファイルとして書き出す。"""

[<EntryPoint>]
let main argv =
    match argv with
    | [| "wikiru-knowledge"; pageName; outputPath |] ->
        let work =
            Browser.withBrowser (fun browser -> Wikiru.writeKnowledge browser pageName outputPath)

        work.GetAwaiter().GetResult()
        0
    | [||] ->
        printfn $"%s{usage}"
        0
    | _ ->
        eprintfn $"%s{usage}"
        1
