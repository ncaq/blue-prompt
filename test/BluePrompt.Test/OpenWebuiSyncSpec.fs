/// open-webui syncの単体テスト。
///
/// 登録や更新が正しく進むことは、
/// 使い捨ての実物のOpen WebUIへ同期するNixOSテスト(tests/open-webui-sync.nix)で確かめる。
/// ここでは実物では起こせない応答や、
/// 登録先へ届く前に止まるべき入力の扱いだけを、
/// 決まった応答を返すだけのサーバを相手に確かめる。
module BluePrompt.Test.OpenWebuiSyncSpec

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Xunit
open BluePrompt
open BluePrompt.OpenWebuiSync

/// 空きポートをOSに割り当てさせて確保する。
/// HttpListenerはポート0の自動割り当てに対応していないため。
/// IPv4とIPv6のどちらで待ち受けても空いているように、両方を受けるソケットで確保する。
let private freePort () : int =
    use listener = TcpListener.Create 0
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// 受け取ったリクエスト1つ分の、テストが確かめる内容。
type private ReceivedRequest =
    { Method: string
      Path: string
      Authorization: string option }

/// メソッドとパスごとに決まった応答を返すサーバ。
/// クエリ文字列は見ず、表に無いリクエストには404を返す。
/// 状態を持たないので、受け取ったリクエストを記録して後から確かめる。
type private FixedServer(routes: Map<string * string, int * string>) =
    let port = freePort ()
    let listener = new HttpListener()
    let received = List<ReceivedRequest>()

    let handle (context: HttpListenerContext) =
        let request = context.Request

        lock received (fun () ->
            received.Add
                { Method = request.HttpMethod
                  Path = request.Url.AbsolutePath
                  Authorization = Option.ofObj request.Headers["Authorization"] })

        // ボディは見ないが、読み切らないとクライアントの送信が終わらないことがある。
        request.InputStream.CopyTo Stream.Null

        let status, body =
            routes
            |> Map.tryFind (request.HttpMethod, request.Url.AbsolutePath)
            |> Option.defaultValue (404, """{"detail":"unknown"}""")

        let response = context.Response
        response.StatusCode <- status
        let bytes = Encoding.UTF8.GetBytes(body: string)
        response.OutputStream.Write(bytes, 0, bytes.Length)
        response.Close()

    do
        listener.Prefixes.Add $"http://localhost:%d{port}/"
        listener.Start()

        Task.Run(fun () ->
            while listener.IsListening do
                try
                    handle (listener.GetContext())
                with _ ->
                    ())
        |> ignore

    member _.Url = $"http://localhost:%d{port}"

    member _.Received = lock received (fun () -> List.ofSeq received)

    /// メソッドとパスが一致したリクエストの数。
    member this.CountOf(method: string, path: string) =
        this.Received
        |> List.filter (fun request -> request.Method = method && request.Path = path)
        |> List.length

    interface IDisposable with
        member _.Dispose() = listener.Close()

/// 起動を待ってサインインするまでの応答。
let private signedIn =
    Map
        [ ("GET", "/health"), (200, """{"status":true}""")
          ("POST", "/api/v1/auths/signin"), (200, """{"token":"signed-in-token"}""") ]

/// Modelが未登録で、作成を受け付ける応答。
/// Open WebUIは未登録のidへ401を返す。
let private modelRoutes =
    signedIn
    |> Map.add ("GET", "/api/v1/models/model") (401, """{"detail":"not found"}""")
    |> Map.add ("POST", "/api/v1/models/create") (200, "{}")

/// Knowledgeコレクションが未登録で、作成とファイルの追加を受け付ける応答。
let private knowledgeRoutes =
    modelRoutes
    |> Map.add ("GET", "/api/v1/knowledge/") (200, """{"items":[],"total":0}""")
    |> Map.add
        ("POST", "/api/v1/knowledge/create")
        (200, """{"id":"collection-id","name":"character-appellation","description":"説明"}""")
    |> Map.add ("GET", "/api/v1/knowledge/collection-id/files") (200, """{"items":[],"total":0}""")
    |> Map.add ("POST", "/api/v1/files/") (200, """{"id":"file-id"}""")
    |> Map.add ("POST", "/api/v1/knowledge/collection-id/file/add") (200, "{}")

/// ModelFormのJSON群をテスト用の一時ディレクトリへ書き出す。
let private makeModelsDirectory (forms: OpenWebui.ModelForm list) : string =
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory directory |> ignore

    for form in forms do
        File.WriteAllText(Path.Combine(directory, $"%s{form.Id}.json"), OpenWebui.toJson form)

    directory

let private makeForm (id: string) : OpenWebui.ModelForm =
    { Id = id
      BaseModelId = None
      Name = id
      Meta =
        { Description = $"%s{id}の説明"
          Knowledge = None }
      Params =
        { System = "プロンプト"
          FunctionCalling = None }
      IsActive = true }

/// knowledge:を書いたスキルから作られるのと同じModelFormを組み立てる。
let private makeFormWithKnowledge (id: string) (knowledgeNames: string list) : OpenWebui.ModelForm =
    let form = makeForm id

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

let private makeOptions (server: FixedServer) (directory: string) : Options =
    { ModelsDirectory = directory
      Url = server.Url
      BaseModelId = None
      ApiKeyFile = None
      KnowledgeDirectory = None
      RagTemplateFile = None }

let private run (options: Options) = (sync options).GetAwaiter().GetResult()

/// 同期がSyncErrorで止まることと、その理由に手掛かりが含まれることを確かめる。
let private assertSyncError (clue: string) (options: Options) =
    let error = Assert.Throws<SyncError>(fun () -> run options)

    match error :> exn with
    | SyncError message -> Assert.Contains(clue, message)
    | unexpected -> failwith unexpected.Message

[<Fact>]
let ``ベースURLの末尾スラッシュはどの経路で組み立てても落とされる`` () =
    use server = new FixedServer(modelRoutes)

    // パスは文字列の連結で組み立てるため、
    // 末尾スラッシュが残ると`//health`のような不正なURLになる。
    // CLI以外の経路がOptionsを組み立てても壊れないように、
    // この不変条件はOptionsを持つ側で保つ。
    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
            Url = $"%s{server.Url}/" }

    run options

    Assert.Equal(1, server.CountOf("POST", "/api/v1/models/create"))

[<Fact>]
let ``APIキーのファイルを指定するとBearerヘッダとして送られる`` () =
    use server = new FixedServer(modelRoutes)
    let apiKeyPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    File.WriteAllText(apiKeyPath, "secret-key\n")

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
            ApiKeyFile = Some apiKeyPath }

    run options

    let authorizations =
        server.Received
        |> List.filter (fun request -> request.Path <> "/health")
        |> List.map _.Authorization
        |> List.distinct

    Assert.Equal<string option list>([ Some "Bearer secret-key" ], authorizations)
    // 資格情報が与えられている以上サインインは要らない。
    Assert.Equal(0, server.CountOf("POST", "/api/v1/auths/signin"))

[<Fact>]
let ``APIキーを指定しないとサインインしたトークンがBearerヘッダとして送られる`` () =
    use server = new FixedServer(modelRoutes)

    run (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]))

    Assert.Equal(1, server.CountOf("POST", "/api/v1/auths/signin"))

    let create =
        server.Received
        |> List.find (fun request -> request.Path = "/api/v1/models/create")

    Assert.Equal(Some "Bearer signed-in-token", create.Authorization)

[<Fact>]
let ``サインインに失敗するとSyncErrorで止まり書き込みへ進まない`` () =
    // 認証を有効にしたインスタンスでは固定の資格情報が通らない。
    use server =
        new FixedServer(
            modelRoutes
            |> Map.add
                ("POST", "/api/v1/auths/signin")
                (400, """{"detail":"Invalid credentials"}""")
        )

    // APIキーの指定が要ることに気付けるようにステータスが含まれる。
    assertSyncError "400" (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]))
    Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))

[<Fact>]
let ``サインインの応答にtokenが無いとSyncErrorで止まる`` () =
    use server =
        new FixedServer(
            modelRoutes
            |> Map.add ("POST", "/api/v1/auths/signin") (200, """{"detail":"ok"}""")
        )

    assertSyncError "token" (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]))
    Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))

[<Fact>]
let ``存在確認のGETがサーバエラーを返すとSyncErrorで止まり作成へ進まない`` () =
    // サーバエラーを未登録とみなして作成へ進むと、意図しない作成につながる。
    use server =
        new FixedServer(
            modelRoutes
            |> Map.add ("GET", "/api/v1/models/model") (500, """{"detail":"internal error"}""")
        )

    // 失敗の理由が調査できるようにステータスコードが含まれる。
    assertSyncError "500" (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]))
    Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))

[<Fact>]
let ``同じidのModel定義が複数あるとSyncErrorで止まる`` () =
    use server = new FixedServer(modelRoutes)
    let directory = makeModelsDirectory [ makeForm "yuuka" ]
    // 別のファイル名で同じidの定義を置く。
    File.WriteAllText(
        Path.Combine(directory, "duplicated.json"),
        OpenWebui.toJson (makeForm "yuuka")
    )

    // どの定義が衝突したのか調査できるようにidが含まれる。
    assertSyncError "yuuka" (makeOptions server directory)
    // 後勝ちの上書きが起きないように、登録先へ触れる前に止まる。
    Assert.Empty(server.Received)

[<Fact>]
let ``同じ名前のKnowledgeコレクションが複数あるとSyncErrorで止まる`` () =
    use server = new FixedServer(knowledgeRoutes)

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
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
            KnowledgeDirectory = Some directory }

    assertSyncError "character-appellation" options
    Assert.Empty(server.Received)

[<Fact>]
let ``紐付け先のKnowledgeが同期の対象に無いとSyncErrorで止まる`` () =
    use server = new FixedServer(knowledgeRoutes)
    let models = makeModelsDirectory [ makeFormWithKnowledge "yuuka" [ "typo-name" ] ]

    let options =
        { makeOptions server models with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]) }

    // 綴りの誤りに気付けるように参照先の名前が含まれる。
    assertSyncError "typo-name" options
    // 参照が外れたModelを黙って登録しない。
    Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))

[<Fact>]
let ``アップロードの応答にidが無いとSyncErrorで止まる`` () =
    // 応答の形が変わった状況を模す。
    // 空のidのまま進むと、コレクションへの追加が空のfile_idで飛んで、
    // 原因から遠い場所で失敗する。
    use server =
        new FixedServer(
            knowledgeRoutes
            |> Map.add ("POST", "/api/v1/files/") (200, """{"detail":"ok"}""")
        )

    let options =
        { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
            KnowledgeDirectory =
                Some(makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]) }

    // 何が足りないのか分かるようにフィールドの名前が含まれる。
    assertSyncError "id" options
    Assert.Equal(0, server.CountOf("POST", "/api/v1/knowledge/collection-id/file/add"))
