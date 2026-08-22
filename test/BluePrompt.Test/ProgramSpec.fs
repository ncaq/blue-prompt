module BluePrompt.Test.ProgramSpec

open System.IO
open Argu
open Xunit
open BluePrompt

[<Fact>]
let ``引数なしでは使い方を表示して0を返す`` () = Assert.Equal(0, Program.main [||])

[<Fact>]
let ``ヘルプの要求は求めた通りの結果なので0を返す`` () =
    Assert.Equal(0, Program.main [| "--help" |])

[<Fact>]
let ``トップレベルの使い方は末端のサブコマンドまで展開する`` () =
    let parser =
        ArgumentParser.Create<Program.RootCommand.Args>(programName = "blue-prompt")

    let usage = Program.expandedUsage parser |> String.concat "\n"

    // グループの一覧で終わらず、末端のコマンドの引数まで一度に読める。
    Assert.Contains("blue-prompt wikiru student-html", usage)
    Assert.Contains("blue-prompt roleplay skill", usage)
    Assert.Contains("blue-prompt open-webui sync", usage)
    Assert.Contains("--rag-template-file", usage)

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
