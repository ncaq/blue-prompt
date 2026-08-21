module BluePrompt.Test.OpenWebuiSyncSpec

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
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

/// Open WebUIがアップロード時にmeta.file_hashへ入れるのと同じ、生バイト列のSHA-256。
let private sha256Hex (bytes: byte array) : string =
    Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData bytes)

/// アップロードされたファイル1つ分の、モックが覚えておく内容。
type private UploadedFile =
    { Id: string
      FileName: string
      Hash: string }

/// multipart/form-dataのボディから、最初のパートのファイル名と中身を取り出す。
/// テストで確かめたいのはアップロードされた中身が変わったかどうかなので、
/// 汎用のパーサーは持ち込まず境界と空行だけを頼りに切り出す。
let private parseMultipart (contentType: string) (body: byte array) : string * byte array =
    let boundary =
        contentType.Split ';'
        |> Array.map _.Trim()
        |> Array.pick (fun part ->
            if part.StartsWith("boundary=", StringComparison.Ordinal) then
                Some("--" + part.Substring("boundary=".Length).Trim '"')
            else
                None)

    let text = Encoding.Latin1.GetString body
    let headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)
    let contentStart = headerEnd + 4

    let contentEnd =
        text.IndexOf("\r\n" + boundary, contentStart, StringComparison.Ordinal)

    // ファイル名はトークンとして書ける文字だけならクォートが付かないため、どちらの形も読む。
    let fileName =
        Regex.Match(text.Substring(0, headerEnd), "filename=\"?([^\";\r\n]*)\"?").Groups[1].Value

    fileName, body[contentStart .. contentEnd - 1]

/// Open WebUIのModel・Knowledge・ファイル・RAG設定のAPIを模したモックサーバ。
/// 登録済みの状態をメモリ上の辞書として持ち、
/// 実物と同じくフォームに無いフィールドがDB側で補われる状況も模す。
type private MockServer() =
    let port = freePort ()
    let listener = new HttpListener()
    let models = Dictionary<string, JsonNode>()
    // コレクションのidから、名前と説明と登録済みファイルへの対応。
    let collections = Dictionary<string, JsonNode>()
    let collectionFiles = Dictionary<string, UploadedFile list>()
    // アップロード済みでまだコレクションへ結び付いていないファイル。
    let mutable uploaded: Map<string, UploadedFile> = Map.empty
    let mutable createCount = 0
    let mutable updateCount = 0
    let mutable signInCount = 0
    let mutable uploadCount = 0
    let mutable fileAddCount = 0
    let mutable fileRemoveCount = 0
    let mutable knowledgeCreateCount = 0
    let mutable ragTemplateUpdateCount = 0
    let mutable ragTemplate = "既定のテンプレート"
    let mutable nextId = 0
    let mutable lastAuthorization: string option = None
    // 存在確認のGETへ固定の応答を返すための上書き。サーバ側の異常を模す。
    let mutable modelGetOverride: (int * string) option = None
    // サインインへ固定の応答を返すための上書き。認証を有効にしたインスタンスを模す。
    let mutable signInOverride: (int * string) option = None
    // アップロードへ固定の応答を返すための上書き。応答の形が変わった状況を模す。
    let mutable uploadOverride: (int * string) option = None

    let issueId () =
        nextId <- nextId + 1
        $"id-%d{nextId}"

    let respond (response: HttpListenerResponse) (status: int) (body: string) =
        response.StatusCode <- status
        let bytes = Encoding.UTF8.GetBytes(body: string)
        response.OutputStream.Write(bytes, 0, bytes.Length)
        response.Close()

    /// リクエストのボディを生バイト列として読み切る。
    let readBody (request: HttpListenerRequest) =
        use buffer = new MemoryStream()
        request.InputStream.CopyTo buffer
        buffer.ToArray()

    let readJson (request: HttpListenerRequest) =
        JsonNode.Parse(Encoding.UTF8.GetString(readBody request))

    /// パスの末尾から2つ目を、コレクションのidとして取り出す。
    let collectionIdOf (path: string) (depthFromEnd: int) =
        let parts = path.Split '/'
        parts[parts.Length - depthFromEnd - 1]

    /// コレクション単体のJSON。
    /// 実物と同じく、この応答のfilesは常にnullで一覧を含まない。
    let collectionJson (id: string) =
        let collection = collections[id].DeepClone()
        collection["files"] <- null
        collection.ToJsonString()

    /// 一覧APIが1ページで返す件数。実物のKnowledgeのファイル一覧と同じ。
    let pageSize = 30

    /// 登録済みファイルの一覧のJSON。
    /// 実物と同じくページングして、itemsと総件数のtotalを持つオブジェクトとして返す。
    let collectionFilesJson (id: string) (page: int) =
        let all = collectionFiles[id]

        let items =
            JsonArray(
                all
                |> List.skip (min ((page - 1) * pageSize) (List.length all))
                |> List.truncate pageSize
                |> List.map (fun file ->
                    let meta = JsonObject()
                    meta["name"] <- JsonValue.Create file.FileName
                    meta["file_hash"] <- JsonValue.Create file.Hash
                    let node = JsonObject()
                    node["id"] <- JsonValue.Create file.Id
                    node["meta"] <- meta
                    node :> JsonNode)
                |> List.toArray
            )

        let root = JsonObject()
        root["items"] <- items
        root["total"] <- JsonValue.Create(List.length all)
        root.ToJsonString()

    /// 実物と同じく、フォームに無いフィールドをDB側で補って保存する。
    let storeNode (form: JsonNode) =
        let meta = form["meta"]
        form["user_id"] <- JsonValue.Create "mock-user"
        meta["profile_image_url"] <- JsonValue.Create "/static/favicon.png"

        // 登録済みのModelには権限の設定が必ず入る。
        if isNull form["access_grants"] then
            form["access_grants"] <- JsonArray()

        models[form["id"].GetValue<string>()] <- form

    let store (request: HttpListenerRequest) =
        use reader = new StreamReader(request.InputStream)
        storeNode (JsonNode.Parse(reader.ReadToEnd()))

    let handle (context: HttpListenerContext) =
        let request = context.Request
        lastAuthorization <- Option.ofObj (request.Headers["Authorization"])

        match request.HttpMethod, request.Url.AbsolutePath with
        | "GET", "/health" -> respond context.Response 200 """{"status":true}"""
        | "POST", "/api/v1/auths/signin" ->
            signInCount <- signInCount + 1

            match signInOverride with
            | Some(status, body) -> respond context.Response status body
            | None -> respond context.Response 200 """{"token":"signed-in-token"}"""
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
            let form = readJson request

            // 実物はaccess_grantsを省いたボディへ500を返す。
            match form["access_grants"] with
            | null -> respond context.Response 500 "Internal Server Error"
            | _ ->
                storeNode form
                respond context.Response 200 "{}"
        | "GET", "/api/v1/knowledge/" ->
            // 実物と同じくitemsを持つオブジェクトとして返す。
            let items = JsonArray(collections.Values |> Seq.map _.DeepClone() |> Seq.toArray)

            let root = JsonObject()
            root["items"] <- items
            respond context.Response 200 (root.ToJsonString())
        | "POST", "/api/v1/knowledge/create" ->
            knowledgeCreateCount <- knowledgeCreateCount + 1
            let form = readJson request
            let id = issueId ()
            form["id"] <- JsonValue.Create id
            collections[id] <- form
            collectionFiles[id] <- []
            respond context.Response 200 (form.ToJsonString())
        | "POST", "/api/v1/files/" ->
            uploadCount <- uploadCount + 1
            let fileName, content = parseMultipart request.ContentType (readBody request)
            let id = issueId ()

            match uploadOverride with
            | Some(status, body) -> respond context.Response status body
            | None ->
                uploaded <-
                    Map.add
                        id
                        { Id = id
                          FileName = fileName
                          Hash = sha256Hex content }
                        uploaded

                let created = JsonObject()
                created["id"] <- JsonValue.Create id
                respond context.Response 200 (created.ToJsonString())
        | "POST", path when path.EndsWith("/file/add", StringComparison.Ordinal) ->
            fileAddCount <- fileAddCount + 1
            let id = collectionIdOf path 2
            let body = readJson request
            let fileId = body["file_id"].GetValue<string>()
            collectionFiles[id] <- collectionFiles[id] @ [ uploaded[fileId] ]
            respond context.Response 200 (collectionJson id)
        | "POST", path when path.EndsWith("/file/remove", StringComparison.Ordinal) ->
            fileRemoveCount <- fileRemoveCount + 1
            let id = collectionIdOf path 2
            let body = readJson request
            let fileId = body["file_id"].GetValue<string>()

            collectionFiles[id] <-
                collectionFiles[id] |> List.filter (fun file -> file.Id <> fileId)

            respond context.Response 200 (collectionJson id)
        | "POST", path when
            path.EndsWith("/update", StringComparison.Ordinal)
            && path.StartsWith("/api/v1/knowledge/", StringComparison.Ordinal)
            ->
            let id = collectionIdOf path 1
            let form = readJson request
            form["id"] <- JsonValue.Create id
            collections[id] <- form
            respond context.Response 200 (collectionJson id)
        | "GET", path when
            path.StartsWith("/api/v1/knowledge/", StringComparison.Ordinal)
            && path.EndsWith("/files", StringComparison.Ordinal)
            ->
            let page =
                match Int32.TryParse(request.QueryString["page"]) with
                | true, value -> value
                | _ -> 1

            respond context.Response 200 (collectionFilesJson (collectionIdOf path 1) page)
        | "GET", path when path.StartsWith("/api/v1/knowledge/", StringComparison.Ordinal) ->
            let id = collectionIdOf path 0

            match collections.ContainsKey id with
            | true -> respond context.Response 200 (collectionJson id)
            | false -> respond context.Response 404 """{"detail":"not found"}"""
        | "GET", "/api/v1/retrieval/config" ->
            let root = JsonObject()
            root["RAG_TEMPLATE"] <- JsonValue.Create ragTemplate
            respond context.Response 200 (root.ToJsonString())
        | "POST", "/api/v1/retrieval/config/update" ->
            ragTemplateUpdateCount <- ragTemplateUpdateCount + 1
            let body = readJson request
            ragTemplate <- body["RAG_TEMPLATE"].GetValue<string>()
            respond context.Response 200 """{"status":true}"""
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

    member _.OverrideModelGet(status: int, body: string) = modelGetOverride <- Some(status, body)
    member _.OverrideSignIn(status: int, body: string) = signInOverride <- Some(status, body)
    member _.OverrideUpload(status: int, body: string) = uploadOverride <- Some(status, body)
    member _.Models = models
    member _.CreateCount = createCount
    member _.UpdateCount = updateCount
    member _.SignInCount = signInCount
    member _.LastAuthorization = lastAuthorization
    member _.PageSize = pageSize
    member _.UploadCount = uploadCount
    member _.FileAddCount = fileAddCount
    member _.FileRemoveCount = fileRemoveCount
    member _.KnowledgeCreateCount = knowledgeCreateCount
    member _.RagTemplateUpdateCount = ragTemplateUpdateCount
    member _.RagTemplate = ragTemplate

    /// コレクションの名前から、登録されているファイル名の一覧を引く。
    member _.FileNamesOf(name: string) =
        collections
        |> Seq.tryPick (fun entry ->
            if entry.Value["name"].GetValue<string>() = name then
                Some(collectionFiles[entry.Key] |> List.map _.FileName)
            else
                None)

    /// コレクションの名前からidを引く。
    member _.CollectionIdOf(name: string) =
        collections
        |> Seq.tryPick (fun entry ->
            if entry.Value["name"].GetValue<string>() = name then
                Some entry.Key
            else
                None)

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
      Meta =
        { Description = $"%s{id}の説明"
          Knowledge = None }
      Params =
        { System = system
          FunctionCalling = None }
      IsActive = true }

let private makeOptions (server: MockServer) (directory: string) : Options =
    { ModelsDirectory = directory
      Url = server.Url
      BaseModelId = None
      ApiKeyFile = None
      KnowledgeDirectory = None
      RagTemplateFile = None }

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
    // 資格情報が与えられている以上サインインは要らない。
    Assert.Equal(0, server.SignInCount)

[<Fact>]
let ``APIキーを指定しないとサインインしたトークンがBearerヘッダとして送られる`` () =
    use server = new MockServer()

    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]))

    Assert.Equal(1, server.SignInCount)
    Assert.Equal(Some "Bearer signed-in-token", server.LastAuthorization)
    Assert.Equal(1, server.CreateCount)

[<Fact>]
let ``サインインに失敗するとSyncErrorで止まり書き込みへ進まない`` () =
    use server = new MockServer()
    // 認証を有効にしたインスタンスでは固定の資格情報が通らない。
    server.OverrideSignIn(400, """{"detail":"Invalid credentials"}""")

    let options = makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ])

    let error = Assert.Throws<SyncError>(fun () -> run options)

    // APIキーの指定が要ることに気付けるように理由が含まれる。
    match error :> exn with
    | SyncError message -> Assert.Contains("400", message)
    | unexpected -> failwith unexpected.Message

    Assert.Equal(0, server.CreateCount)

[<Fact>]
let ``サインインの応答にtokenが無いとSyncErrorで止まる`` () =
    use server = new MockServer()
    server.OverrideSignIn(200, """{"detail":"ok"}""")

    let options = makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ])

    let error = Assert.Throws<SyncError>(fun () -> run options)

    match error :> exn with
    | SyncError message -> Assert.Contains("token", message)
    | unexpected -> failwith unexpected.Message

    Assert.Equal(0, server.CreateCount)

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
let ``応答に権限設定が無いと空の権限で上書きせずSyncErrorで止まる`` () =
    use server = new MockServer()

    // access_grantsを持たない応答を模す。
    // 実物のModelModelはこのフィールドを必ず埋めて返すため、
    // 欠けているのは応答の形が想定と違うということで、
    // このリポジトリが管理しない権限の設定を推測で書き換えてはいけない。
    server.OverrideModelGet(
        200,
        """{"id":"yuuka","name":"yuuka","meta":{"description":"yuukaの説明"},"params":{"system":"古いプロンプト"},"is_active":true}"""
    )

    let options =
        makeOptions server (makeModelsDirectory [ makeForm "yuuka" "改良したプロンプト" ])

    let error = Assert.Throws<SyncError>(fun () -> run options)

    // 何が足りないのか分かるようにフィールドの名前が含まれる。
    match error :> exn with
    | SyncError message -> Assert.Contains("access_grants", message)
    | unexpected -> failwith unexpected.Message

    Assert.Equal(0, server.UpdateCount)

[<Fact>]
let ``同じidのModel定義が複数あるとSyncErrorで止まる`` () =
    use server = new MockServer()
    let directory = makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]
    // 別のファイル名で同じidの定義を置く。
    File.WriteAllText(
        Path.Combine(directory, "duplicated.json"),
        OpenWebui.toJson (makeForm "yuuka" "別のプロンプト")
    )

    let error = Assert.Throws<SyncError>(fun () -> run (makeOptions server directory))

    // どの定義が衝突したのか調査できるようにidが含まれる。
    match error :> exn with
    | SyncError message -> Assert.Contains("yuuka", message)
    | unexpected -> failwith unexpected.Message

    // 後勝ちの上書きが起きないように書き込みの前に止まる。
    Assert.Equal(0, server.CreateCount)

[<Fact>]
let ``解釈できない引数はSyncErrorになる`` () =
    Assert.Throws<SyncError>(fun () ->
        parseOptions [ "/tmp/models"; "http://127.0.0.1:8080"; "--unknown" ] |> ignore)
    |> ignore

/// Knowledgeコレクションの定義一式をテスト用の一時ディレクトリへ書き出す。
let private makeKnowledgeDirectory (collections: (string * (string * string) list) list) : string =
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())

    for name, files in collections do
        let collectionDirectory = Path.Combine(directory, name)

        let filesDirectory =
            Path.Combine(collectionDirectory, OpenWebuiKnowledge.filesDirectoryName)

        Directory.CreateDirectory filesDirectory |> ignore

        File.WriteAllText(
            Path.Combine(collectionDirectory, OpenWebuiKnowledge.formFileName),
            OpenWebuiKnowledge.toJson
                { Name = name
                  Description = $"%s{name}の説明" }
        )

        for fileName, content in files do
            File.WriteAllText(Path.Combine(filesDirectory, fileName), content)

    directory

/// knowledge:を書いたスキルから作られるのと同じModelFormを組み立てる。
let private makeFormWithKnowledge (id: string) (knowledgeNames: string list) : OpenWebui.ModelForm =
    let form = makeForm id "プロンプト"

    { form with
        Meta =
            { form.Meta with
                Knowledge =
                    Some(
                        knowledgeNames
                        |> List.map (fun name ->
                            { OpenWebui.KnowledgeReference.Id = None
                              Name = name
                              Type = OpenWebui.collectionType })
                    ) }
        Params =
            { form.Params with
                FunctionCalling = Some OpenWebui.legacyFunctionCalling } }

[<Fact>]
let ``未登録のKnowledgeは作成され再実行では書き込まれない`` () =
    use server = new MockServer()

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]) with
            KnowledgeDirectory =
                Some(
                    makeKnowledgeDirectory
                        [ "character-appellation", [ "a.md", "呼称A"; "b.md", "呼称B" ] ]
                ) }

    run options
    Assert.Equal(1, server.KnowledgeCreateCount)
    Assert.Equal(2, server.UploadCount)
    Assert.Equal(2, server.FileAddCount)

    Assert.Equal<string list option>(
        Some [ "a.md"; "b.md" ],
        server.FileNamesOf "character-appellation"
    )

    // 中身が変わっていないファイルはハッシュが一致するので触らない。
    run options
    Assert.Equal(1, server.KnowledgeCreateCount)
    Assert.Equal(2, server.UploadCount)
    Assert.Equal(2, server.FileAddCount)
    Assert.Equal(0, server.FileRemoveCount)

[<Fact>]
let ``中身が変わったファイルは登録し直される`` () =
    use server = new MockServer()
    let models = makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]

    run
        { makeOptions server models with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "古い呼称" ] ]) }

    run
        { makeOptions server models with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "新しい呼称" ] ]) }

    // 古い埋め込みが残らないように、消してから入れ直す。
    Assert.Equal(1, server.FileRemoveCount)
    Assert.Equal(2, server.UploadCount)
    Assert.Equal(2, server.FileAddCount)

[<Fact>]
let ``生成物から消えたファイルはコレクションからも取り除かれる`` () =
    use server = new MockServer()
    let models = makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]

    run
        { makeOptions server models with
            KnowledgeDirectory =
                Some(
                    makeKnowledgeDirectory
                        [ "character-appellation", [ "a.md", "呼称A"; "b.md", "呼称B" ] ]
                ) }

    run
        { makeOptions server models with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]) }

    Assert.Equal(1, server.FileRemoveCount)
    Assert.Equal<string list option>(Some [ "a.md" ], server.FileNamesOf "character-appellation")

[<Fact>]
let ``Modelの紐付けには作成したコレクションのidが埋まる`` () =
    use server = new MockServer()

    let models =
        makeModelsDirectory [ makeFormWithKnowledge "yuuka" [ "character-appellation" ] ]

    run
        { makeOptions server models with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]) }

    let knowledge = node server.Models["yuuka"] [ "meta"; "knowledge" ]
    let reference = knowledge[0]

    Assert.Equal(
        server.CollectionIdOf "character-appellation",
        Some(reference["id"].GetValue<string>())
    )

    Assert.Equal("collection", reference["type"].GetValue<string>())
    // 自動でRAG検索させるための方式もModelへ設定される。
    Assert.Equal(
        OpenWebui.legacyFunctionCalling,
        (node server.Models["yuuka"] [ "params"; "function_calling" ]).GetValue<string>()
    )

[<Fact>]
let ``紐付け先のKnowledgeが同期の対象に無いとSyncErrorで止まる`` () =
    use server = new MockServer()

    let models = makeModelsDirectory [ makeFormWithKnowledge "yuuka" [ "typo-name" ] ]

    let options =
        { makeOptions server models with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]) }

    let error = Assert.Throws<SyncError>(fun () -> run options)

    // 綴りの誤りに気付けるように参照先の名前が含まれる。
    match error :> exn with
    | SyncError message -> Assert.Contains("typo-name", message)
    | unexpected -> failwith unexpected.Message

    // 参照が外れたModelを黙って登録しない。
    Assert.Equal(0, server.CreateCount)

[<Fact>]
let ``アップロードの応答にidが無いとSyncErrorで止まる`` () =
    use server = new MockServer()
    // 応答の形が変わった状況を模す。
    // 空のidのまま進むと、コレクションへの追加が空のfile_idで飛んで、
    // 原因から遠い場所で失敗する。
    server.OverrideUpload(200, """{"detail":"ok"}""")

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]) with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]) }

    let error = Assert.Throws<SyncError>(fun () -> run options)

    // 何が足りないのか分かるようにフィールドの名前が含まれる。
    match error :> exn with
    | SyncError message -> Assert.Contains("id", message)
    | unexpected -> failwith unexpected.Message

    Assert.Equal(0, server.FileAddCount)

[<Fact>]
let ``RAGテンプレートは差分がある時だけ書き込まれる`` () =
    use server = new MockServer()
    let templatePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    File.WriteAllText(templatePath, "参考資料:\n{{CONTEXT}}\n")

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]) with
            RagTemplateFile = Some templatePath }

    run options
    Assert.Equal(1, server.RagTemplateUpdateCount)
    Assert.Equal("参考資料:\n{{CONTEXT}}\n", server.RagTemplate)

    run options
    Assert.Equal(1, server.RagTemplateUpdateCount)

[<Fact>]
let ``同じ名前のKnowledgeコレクションが複数あるとSyncErrorで止まる`` () =
    use server = new MockServer()

    let directory =
        makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]

    // 別のディレクトリ名で同じ名前の定義を置く。
    let duplicated = Path.Combine(directory, "duplicated")

    Directory.CreateDirectory(Path.Combine(duplicated, OpenWebuiKnowledge.filesDirectoryName))
    |> ignore

    File.WriteAllText(
        Path.Combine(duplicated, OpenWebuiKnowledge.formFileName),
        OpenWebuiKnowledge.toJson
            { Name = "character-appellation"
              Description = "重複した定義" }
    )

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]) with
            KnowledgeDirectory = Some directory }

    let error = Assert.Throws<SyncError>(fun () -> run options)

    match error :> exn with
    | SyncError message -> Assert.Contains("character-appellation", message)
    | unexpected -> failwith unexpected.Message

    Assert.Equal(0, server.KnowledgeCreateCount)

[<Fact>]
let ``一覧の1ページに収まらない数のファイルでも再実行で書き込まれない`` () =
    use server = new MockServer()

    // 一覧APIは1ページ分しか返さないため、
    // ページを辿らないと登録済みのファイルを未登録と取り違えて、
    // 同じ内容を登録し直そうとしてDuplicate contentで弾かれる。
    let pageSize = server.PageSize

    let files =
        List.init (pageSize + 5) (fun index -> $"file%02d{index}.md", $"呼称%d{index}")

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" "プロンプト" ]) with
            KnowledgeDirectory = Some(makeKnowledgeDirectory [ "character-appellation", files ]) }

    run options
    Assert.Equal(pageSize + 5, server.UploadCount)
    Assert.Equal(pageSize + 5, server.FileAddCount)

    run options
    Assert.Equal(pageSize + 5, server.UploadCount)
    Assert.Equal(pageSize + 5, server.FileAddCount)
    Assert.Equal(0, server.FileRemoveCount)

[<Fact>]
let ``更新でも登録済みの権限設定が保たれる`` () =
    use server = new MockServer()
    let models = makeModelsDirectory [ makeForm "yuuka" "古いプロンプト" ]
    run (makeOptions server models)

    // 登録後にUIで公開範囲を設定した状況を模す。
    let grants = JsonArray()
    grants.Add(JsonValue.Create "everyone")
    server.Models["yuuka"]["access_grants"] <- grants

    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" "改良したプロンプト" ]))

    Assert.Equal(1, server.UpdateCount)
    // 管理対象ではない権限の設定は読み取った値をそのまま送り返して保つ。
    let stored = node server.Models["yuuka"] [ "access_grants" ]
    Assert.Equal("everyone", stored[0].GetValue<string>())
