module BluePrompt.Test.ProgramSpec

open System
open System.IO
open Argu
open Xunit
open BluePrompt

/// 末端のサブコマンドの、コマンド名とそこへ辿り着く引数の組。
/// 判別共用体で宣言した順に並べる。
/// ヘルプの並びもケースの並びから決まるため、並び順の期待もこの表が持つ。
let private commands =
    [ "wikiru appellation",
      [| "wikiru"
         "appellation"
         "--page"
         "キャラ呼称表"
         "--markdown-output"
         "reference.md"
         "--json-output"
         "appellation.json" |]
      "wikiru knowledge", [| "wikiru"; "knowledge"; "--page"; "ページ"; "--output"; "out.md" |]
      "wikiru roleplay-reference",
      [| "wikiru"; "roleplay-reference"; "--page"; "ページ"; "--output"; "out.md" |]
      "wikiru school", [| "wikiru"; "school"; "--page"; "学校別"; "--output"; "out.md" |]
      "wikiru student-skill",
      [| "wikiru"; "student-skill"; "--page"; "ページ"; "--output"; "SKILL.md" |]
      "wikiru html", [| "wikiru"; "html"; "--page"; "ページ"; "--output"; "out.html" |]
      "wikiru student-html", [| "wikiru"; "student-html"; "--page"; "ページ"; "--output"; "out.html" |]
      "roleplay skill",
      [| "roleplay"
         "skill"
         "--character"
         "ユウカ"
         "--template"
         "plugins/role-play"
         "--appellation"
         "appellation.json"
         "--output"
         "plugins/role-play/skills/yuuka" |]
      "open-webui knowledge",
      [| "open-webui"; "knowledge"; "--skill"; "skills/yuuka"; "--output"; "out" |]
      "open-webui model",
      [| "open-webui"; "model"; "--skill"; "skills/yuuka"; "--output"; "out.json" |]
      "open-webui sync",
      [| "open-webui"
         "sync"
         "--model"
         "models"
         "--base-url"
         "http://127.0.0.1:8080" |] ]

/// パースの結果を、表のコマンド名と突き合わせられる文字列にする。
/// 同じ型の引数を取るケースが横に並ぶため、
/// 取り違えても型では気付けないのをここで名前へ落として固定する。
let private parsedName (argv: string array) : string =
    let parser =
        ArgumentParser.Create<Program.RootCommand.Args>(programName = "blue-prompt")

    match (parser.ParseCommandLine argv).GetSubCommand() with
    | Program.RootCommand.Wikiru sub ->
        match sub.GetSubCommand() with
        | Program.WikiruCommand.Appellation _ -> "wikiru appellation"
        | Program.WikiruCommand.Knowledge _ -> "wikiru knowledge"
        | Program.WikiruCommand.Roleplay_Reference _ -> "wikiru roleplay-reference"
        | Program.WikiruCommand.School _ -> "wikiru school"
        | Program.WikiruCommand.Student_Skill _ -> "wikiru student-skill"
        | Program.WikiruCommand.Html _ -> "wikiru html"
        | Program.WikiruCommand.Student_Html _ -> "wikiru student-html"
    | Program.RootCommand.Roleplay sub ->
        match sub.GetSubCommand() with
        | Program.RolePlayCommand.Skill _ -> "roleplay skill"
    | Program.RootCommand.Open_Webui sub ->
        match sub.GetSubCommand() with
        | Program.OpenWebuiCommand.Knowledge _ -> "open-webui knowledge"
        | Program.OpenWebuiCommand.Model _ -> "open-webui model"
        | Program.OpenWebuiCommand.Sync _ -> "open-webui sync"

[<Fact>]
let ``引数なしでは使い方を表示して0を返す`` () = Assert.Equal(0, Program.main [||])

[<Fact>]
let ``ヘルプの要求は求めた通りの結果なので0を返す`` () =
    Assert.Equal(0, Program.main [| "--help" |])

[<Fact>]
let ``トップレベルの使い方は末端の引数まで展開する`` () =
    let parser =
        ArgumentParser.Create<Program.RootCommand.Args>(programName = "blue-prompt")

    let usage = Program.expandedUsage parser |> String.concat "\n"

    // グループの一覧で終わらず、末端のコマンドの引数まで一度に読める。
    Assert.Contains("--rag-template-file", usage)

[<Fact>]
let ``展開した使い方には全ての末端のサブコマンドが宣言順で並ぶ`` () =
    let parser =
        ArgumentParser.Create<Program.RootCommand.Args>(programName = "blue-prompt")

    let usage = Program.expandedUsage parser |> String.concat "\n"

    // ケースを足してもヘルプへ出ていなければ、ここで引けない。
    let positions =
        commands
        |> List.map (fun (name, _) ->
            let index = usage.IndexOf($"blue-prompt %s{name}", StringComparison.Ordinal)
            Assert.True(0 <= index, $"%s{name}が使い方に出ていません")
            index)

    // 宣言順に並んでいることを、出現位置が単調増加することで確かめる。
    Assert.Equal<int list>(List.sort positions, positions)

[<Fact>]
let ``コマンド名は判別共用体のケースへ対応する`` () =
    for name, argv in commands do
        Assert.Equal(name, parsedName argv)

[<Fact>]
let ``未知のサブコマンドでは非0を返す`` () =
    Assert.Equal(1, Program.main [| "unknown-subcommand" |])

[<Fact>]
let ``グループはあるが未知のサブコマンドでは非0を返す`` () =
    Assert.Equal(1, Program.main [| "wikiru"; "unknown-subcommand" |])

[<Fact>]
let ``必須の引数が足りないopen-webui syncは非0を返す`` () =
    Assert.Equal(1, Program.main [| "open-webui"; "sync" |])

[<Fact>]
let ``既知のサブコマンドへの未知のオプションでは非0を返す`` () =
    // オプション名のタイポが黙って無視されて同期処理へ進んでしまわない。
    Assert.Equal(
        1,
        Program.main
            [| "open-webui"
               "sync"
               "--model"
               "/tmp/models"
               "--base-url"
               "http://127.0.0.1:8080"
               "--unknown" |]
    )

[<Fact>]
let ``階層を指定したヘルプの要求も0を返す`` () =
    // 引数が2つあるので手前の全展開では拾われず、ArguのHelpTextの分岐を通る。
    Assert.Equal(0, Program.main [| "wikiru"; "--help" |])

[<Fact>]
let ``同期の失敗は理由だけを表示して非0を返す`` () =
    let temporary = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let models = Path.Combine(temporary, "models")
    let knowledge = Path.Combine(temporary, "knowledge")
    Directory.CreateDirectory models |> ignore
    // 定義のファイルを欠いたコレクションのディレクトリで、生成物の読み込みを失敗させる。
    // 起動待ちより前に落ちるので、インスタンスが無くてもこの分岐を通せる。
    Directory.CreateDirectory(Path.Combine(knowledge, "broken")) |> ignore

    Assert.Equal(
        1,
        Program.main
            [| "open-webui"
               "sync"
               "--model"
               models
               "--base-url"
               "http://127.0.0.1:8080"
               "--knowledge"
               knowledge |]
    )

[<Fact>]
let ``コマンドライン引数から接続情報を組み立てられる`` () =
    let parser = ArgumentParser.Create<Program.Sync.Args>()

    let options =
        Program.syncOptions (
            parser.Parse
                [| "--model"
                   "/tmp/models"
                   "--base-url"
                   "http://127.0.0.1:8080/"
                   "--base-model-id"
                   "qwen3:32b"
                   "--api-key-file"
                   "/run/credentials/api-key"
                   "--knowledge"
                   "/tmp/knowledge"
                   "--rag-template-file"
                   "/tmp/rag-template.txt" |]
        )

    Assert.Equal("/tmp/models", options.ModelsDirectory)
    // 末尾スラッシュの正規化はOpenWebuiSync側の責務なので、ここでは素通しする。
    Assert.Equal("http://127.0.0.1:8080/", options.Url)
    Assert.Equal(Some "qwen3:32b", options.BaseModelId)
    Assert.Equal(Some "/run/credentials/api-key", options.ApiKeyFile)
    Assert.Equal(Some "/tmp/knowledge", options.KnowledgeDirectory)
    Assert.Equal(Some "/tmp/rag-template.txt", options.RagTemplateFile)

[<Fact>]
let ``省略できるオプションを渡さなければNoneのままになる`` () =
    let parser = ArgumentParser.Create<Program.Sync.Args>()

    let options =
        Program.syncOptions (
            parser.Parse [| "--model"; "/tmp/models"; "--base-url"; "http://127.0.0.1:8080" |]
        )

    // ModelだけをKnowledgeやRAGテンプレート抜きで同期する運用が成り立つ。
    Assert.Equal(None, options.BaseModelId)
    Assert.Equal(None, options.ApiKeyFile)
    Assert.Equal(None, options.KnowledgeDirectory)
    Assert.Equal(None, options.RagTemplateFile)
