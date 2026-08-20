module BluePrompt.Test.OpenWebuiSyncSpec

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open BluePrompt
open BluePrompt.OpenWebuiSync

/// loopbackの空きポートをOSに割り当てさせて確保する。
/// HttpListenerはポート0の自動割り当てに対応していないため。
let private freePort () : int =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// Open WebUIのModel APIを模したモックサーバ。
/// 登録済みModelをメモリ上の辞書として持ち、
/// 実物と同じくフォームに無いフィールドがDB側で補われる状況も模す。
type private MockServer() =
    let port = freePort ()
    let listener = new HttpListener()
    let models = Dictionary<string, JsonNode>()
    let mutable createCount = 0
    let mutable updateCount = 0
    let mutable lastAuthorization: string option = None
    // 存在確認のGETへ固定の応答を返すための上書き。サーバ側の異常を模す。
    let mutable modelGetOverride: (int * string) option = None

    let respond (response: HttpListenerResponse) (status: int) (body: string) =
        response.StatusCode <- status
        let bytes = Encoding.UTF8.GetBytes(body: string)
        response.OutputStream.Write(bytes, 0, bytes.Length)
        response.Close()

    let store (request: HttpListenerRequest) =
        use reader = new StreamReader(request.InputStream)
        let form = JsonNode.Parse(reader.ReadToEnd())
        let meta = form["meta"]
        form["user_id"] <- JsonValue.Create "mock-user"
        meta["profile_image_url"] <- JsonValue.Create "/static/favicon.png"
        models[form["id"].GetValue<string>()] <- form

    let handle (context: HttpListenerContext) =
        let request = context.Request
        lastAuthorization <- Option.ofObj (request.Headers["Authorization"])

        match request.HttpMethod, request.Url.AbsolutePath with
        | "GET", "/health" -> respond context.Response 200 """{"status":true}"""
        | "GET", "/api/v1/models/model" ->
            match modelGetOverride with
            | Some(status, body) -> respond context.Response status body
            | None ->
                match models.TryGetValue(request.QueryString["id"]) with
                | true, model -> respond context.Response 200 (model.ToJsonString())
                // Open WebUIは未登録のidへ401を返す。
                | _ -> respond context.Response 401 """{"detail":"not found"}"""
        | "POST", "/api/v1/models/create" ->
            createCount <- createCount + 1
            store request
            respond context.Response 200 "{}"
        | "POST", "/api/v1/models/model/update" ->
            updateCount <- updateCount + 1
            store request
            respond context.Response 200 "{}"
        | _ -> respond context.Response 404 """{"detail":"unknown"}"""

    do
        listener.Prefixes.Add $"http://127.0.0.1:%d{port}/"
        listener.Start()

        Task.Run(fun () ->
            while listener.IsListening do
                try
                    handle (listener.GetContext())
                with _ ->
                    ())
        |> ignore

    member _.Url = $"http://127.0.0.1:%d{port}"

    member _.OverrideModelGet(status: int, body: string) =
        modelGetOverride <- Some(status, body)
    member _.Models = models
    member _.CreateCount = createCount
    member _.UpdateCount = updateCount
    member _.LastAuthorization = lastAuthorization

    interface IDisposable with
        member _.Dispose() = listener.Close()

/// ModelFormのJSON群をテスト用の一時ディレクトリへ書き出す。
let private makeModelsDirectory (forms: OpenWebui.ModelForm list) : string =
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory directory |> ignore

    for form in forms do
        File.WriteAllText(Path.Combine(directory, $"%s{form.Id}.json"), OpenWebui.toJson form)

    directory

let private makeForm (id: string) (system: string) : OpenWebui.ModelForm =
    { Id = id
      BaseModelId = None
      Name = id
      Meta = { Description = $"%s{id}の説明" }
      Params = { System = system }
      IsActive = true }

let private makeOptions (server: MockServer) (directory: string) : Options =
    { ModelsDirectory = directory
      Url = server.Url
      BaseModelId = None
      ApiKeyFile = None }

let private run (options: Options) = (sync options).GetAwaiter().GetResult()

/// JsonNodeをネストしたキーで辿る。
/// F#はインデクサの連鎖を式の位置によっては解釈できないため、関数として用意する。
let private node (root: JsonNode) (keys: string list) : JsonNode =
    List.fold (fun (current: JsonNode) (key: string) -> current[key]) root keys

[<Fact>]
let ``未登録のModelは作成され再実行では書き込まれない`` () =
    use server = new MockServer()

    let options =
        makeOptions
            server
            (makeModelsDirectory [ makeForm "yuuka" "プロンプト"; makeForm "kotori" "プロンプト" ])

    run options
    Assert.Equal(2, server.CreateCount)
    Assert.Equal(0, server.UpdateCount)

    run options
    Assert.Equal(2, server.CreateCount)
    Assert.Equal(0, server.UpdateCount)

[<Fact>]
let ``スキルを改良すると登録済みのModelが上書きされる`` () =
    use server = new MockServer()
    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" "古いプロンプト" ]))

    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" "改良したプロンプト" ]))

    Assert.Equal(1, server.UpdateCount)
    let yuuka = server.Models["yuuka"]
    Assert.Equal("改良したプロンプト", (node yuuka [ "params"; "system" ]).GetValue<string>())

[<Fact>]
let ``base-model-id未指定ではUIで選ばれた上流モデルを保持したまま改良を上書きする`` () =
    use server = new MockServer()
    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" "古いプロンプト" ]))

    // 登録後にUIで上流モデルを選んだ状況を模す。
    let yuuka = server.Models["yuuka"]
    yuuka["base_model_id"] <- JsonValue.Create "qwen3:32b"

    // 上流モデルの選択だけでは差分とみなさず書き込まない。
    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" "古いプロンプト" ]))
    Assert.Equal(0, server.UpdateCount)

    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" "改良したプロンプト" ]))
    Assert.Equal(1, server.UpdateCount)
    let updated = server.Models["yuuka"]
    Assert.Equal("qwen3:32b", (node updated [ "base_model_id" ]).GetValue<string>())
    Assert.Equal("改良したプロンプト", (node updated [ "params"; "system" ]).GetValue<string>())

[<Fact>]
let ``base-model-idを指定すると作成時から設定される`` () =
    use server = new MockServer()

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]) with
            BaseModelId = Some "qwen3:32b" }

    run options

    let yuuka = server.Models["yuuka"]
    Assert.Equal("qwen3:32b", (node yuuka [ "base_model_id" ]).GetValue<string>())

[<Fact>]
let ``APIキーのファイルを指定するとBearerヘッダとして送られる`` () =
    use server = new MockServer()
    let apiKeyPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    File.WriteAllText(apiKeyPath, "secret-key\n")

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]) with
            ApiKeyFile = Some apiKeyPath }

    run options

    Assert.Equal(Some "Bearer secret-key", server.LastAuthorization)

[<Fact>]
let ``コマンドライン引数から接続情報を組み立てられる`` () =
    let options =
        parseOptions
            [ "/tmp/models"
              "http://127.0.0.1:8080/"
              "--base-model-id"
              "qwen3:32b"
              "--api-key-file"
              "/run/credentials/api-key" ]

    Assert.Equal("/tmp/models", options.ModelsDirectory)
    // 末尾スラッシュは落とされる。
    Assert.Equal("http://127.0.0.1:8080", options.Url)
    Assert.Equal(Some "qwen3:32b", options.BaseModelId)
    Assert.Equal(Some "/run/credentials/api-key", options.ApiKeyFile)

[<Fact>]
let ``存在確認のGETがサーバエラーを返すとSyncErrorで止まり作成へ進まない`` () =
    use server = new MockServer()
    server.OverrideModelGet(500, """{"detail":"internal error"}""")

    let options = makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ])

    let error = Assert.Throws<SyncError>(fun () -> run options)

    // 失敗の理由が調査できるようにステータスコードが含まれる。
    match error :> exn with
    | SyncError message -> Assert.Contains("500", message)
    | unexpected -> failwith unexpected.Message

    Assert.Equal(0, server.CreateCount)

[<Fact>]
let ``解釈できない引数はSyncErrorになる`` () =
    Assert.Throws<SyncError>(fun () ->
        parseOptions [ "/tmp/models"; "http://127.0.0.1:8080"; "--unknown" ] |> ignore)
    |> ignore
