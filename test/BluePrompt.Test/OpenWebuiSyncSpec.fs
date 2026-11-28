/// open-webui syncの単体テスト。
///
/// 登録や更新が正しく進むことは、
/// 使い捨ての実物のOpen WebUIへ同期するNixOSテスト(tests/open-webui-sync.nix)で確かめる。
/// ここでは実物では起こせない応答や、
/// 登録先へ届く前に止まるべき入力の扱いだけを、
/// 決まった応答を返すだけのサーバを相手に確かめる。
module BluePrompt.Test.OpenWebuiSyncSpec

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open WireMock.RequestBuilders
open WireMock.ResponseBuilders
open WireMock.Server
open Xunit
open BluePrompt
open BluePrompt.OpenWebuiSync

/// 受け取ったリクエスト1つ分の、テストが確かめる内容。
type private ReceivedRequest =
    { Method: string
      Path: string
      Authorization: string option }

/// 決まった応答を返すサーバの、テストから見える面。
type private FixedServer =
    {
        /// 末尾のスラッシュを含まないベースURL。
        Url: string
        /// ここまでに受け取ったリクエスト。
        Received: unit -> ReceivedRequest list
        /// 表では書けない、回数で変わる応答を足すためのWireMockのサーバ。
        Mock: WireMockServer
    }

    /// メソッドとパスが一致したリクエストの数。
    member this.CountOf(method: string, path: string) =
        this.Received()
        |> List.filter (fun request -> request.Method = method && request.Path = path)
        |> List.length

/// メソッドとパスごとに決まった応答を返すサーバを立てて、testへ渡す。
/// クエリ文字列は見ず、表に無いリクエストにはWireMockの既定の404を返す。
/// 表の応答は状態を持たず、回数で変わる応答はテスト側がMockへ足す。
/// 受け取ったリクエストはWireMockの記録から後で確かめる。
let private withFixedServer
    (routes: Map<string * string, int * string>)
    (test: FixedServer -> Task<unit>)
    : Task =
    task {
        use server = WireMockServer.Start()

        for KeyValue((method, path), (status, body)) in routes do
            server
                .Given(Request.Create().UsingMethod(method).WithPath(path))
                .RespondWith(
                    Response
                        .Create()
                        .WithStatusCode(status)
                        .WithHeader("Content-Type", "application/json")
                        .WithBody(body)
                )
            |> ignore

        let received () =
            server.LogEntries
            |> Seq.map (fun entry ->
                let request = entry.RequestMessage

                let authorization =
                    match request.Headers.TryGetValue "Authorization" with
                    | true, values -> Seq.tryHead values
                    | false, _ -> None

                { Method = request.Method
                  Path = request.Path
                  Authorization = authorization })
            |> List.ofSeq

        do!
            test
                { Url = server.Url.TrimEnd '/'
                  Received = received
                  Mock = server }
    }

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

/// `knowledgeRoutes`が作成を受け付けるコレクションを1つだけ持つ定義一式。
let private makeAppellationDirectory () : string =
    makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]

let private makeOptions (server: FixedServer) (directory: string) : Options =
    { ModelsDirectory = directory
      Url = server.Url
      BaseModelId = None
      ApiKeyFile = None
      KnowledgeDirectory = None
      RagTemplateFile = None }

/// 同期がSyncErrorで止まることと、その理由に手掛かりが全て含まれることを確かめる。
/// 短い手掛かりは別の失敗の理由にも一致し得るので、
/// どこで止まったのかが分かるように止まった先のURLなども並べる。
let private assertSyncError (clues: string list) (options: Options) : Task<unit> =
    task {
        let! error = Assert.ThrowsAsync<SyncError>(fun () -> sync options :> Task)

        match error :> exn with
        | SyncError message ->
            for clue in clues do
                Assert.Contains(clue, message)
        | unexpected -> failwith unexpected.Message
    }

[<Fact>]
let ``ベースURLの末尾スラッシュはどの経路で組み立てても落とされる`` () : Task =
    withFixedServer modelRoutes (fun server ->
        task {
            // パスは文字列の連結で組み立てるため、
            // 末尾スラッシュが残ると`//health`のような不正なURLになる。
            // CLI以外の経路がOptionsを組み立てても壊れないように、
            // この不変条件はOptionsを持つ側で保つ。
            let options =
                { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
                    Url = $"%s{server.Url}/" }

            do! sync options

            Assert.Equal(1, server.CountOf("POST", "/api/v1/models/create"))
        })

[<Fact>]
let ``起動の確認が成功するまでやり直してから同期する`` () : Task =
    // 起動の途中のインスタンスは、前段のプロキシなどから成功以外の応答を返す。
    // やり直しの待ちの2秒を1回だけ消費する。
    withFixedServer (Map.remove ("GET", "/health") modelRoutes) (fun server ->
        task {
            let health = Request.Create().UsingGet().WithPath("/health")

            server.Mock
                .Given(health)
                .InScenario("起動")
                .WillSetStateTo("起動済み")
                .RespondWith(Response.Create().WithStatusCode(503))
            |> ignore

            server.Mock
                .Given(health)
                .InScenario("起動")
                .WhenStateIs("起動済み")
                .RespondWith(Response.Create().WithStatusCode(200))
            |> ignore

            do! sync (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]))

            Assert.Equal(2, server.CountOf("GET", "/health"))
            Assert.Equal(1, server.CountOf("POST", "/api/v1/models/create"))
        })

[<Fact>]
let ``APIキーのファイルを指定するとBearerヘッダとして送られる`` () : Task =
    withFixedServer modelRoutes (fun server ->
        task {
            let apiKeyPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
            File.WriteAllText(apiKeyPath, "secret-key\n")

            let options =
                { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
                    ApiKeyFile = Some apiKeyPath }

            do! sync options

            let authorizations =
                server.Received()
                |> List.filter (fun request -> request.Path <> "/health")
                |> List.map _.Authorization
                |> List.distinct

            Assert.Equal<string option list>([ Some "Bearer secret-key" ], authorizations)
            // 資格情報が与えられている以上サインインは要らない。
            Assert.Equal(0, server.CountOf("POST", "/api/v1/auths/signin"))
        })

[<Fact>]
let ``APIキーを指定しないとサインインしたトークンがBearerヘッダとして送られる`` () : Task =
    withFixedServer modelRoutes (fun server ->
        task {
            do! sync (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]))

            Assert.Equal(1, server.CountOf("POST", "/api/v1/auths/signin"))

            let create =
                server.Received()
                |> List.find (fun request -> request.Path = "/api/v1/models/create")

            Assert.Equal(Some "Bearer signed-in-token", create.Authorization)
        })

[<Fact>]
let ``サインインに失敗すると止まり書き込みへ進まない`` () : Task =
    // 認証を有効にしたインスタンスでは固定の資格情報が通らない。
    let routes =
        modelRoutes
        |> Map.add ("POST", "/api/v1/auths/signin") (400, """{"detail":"Invalid credentials"}""")

    withFixedServer routes (fun server ->
        task {
            let! error =
                Assert.ThrowsAsync<HttpRequestException>(fun () ->
                    sync (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ])) :> Task)

            Assert.Equal(Nullable HttpStatusCode.BadRequest, error.StatusCode)
            Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))
        })

[<Fact>]
let ``サインインの応答にtokenが無いと止まる`` () : Task =
    let routes =
        modelRoutes
        |> Map.add ("POST", "/api/v1/auths/signin") (200, """{"detail":"ok"}""")

    withFixedServer routes (fun server ->
        task {
            let! error =
                Assert.ThrowsAsync<JsonException>(fun () ->
                    sync (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ])) :> Task)

            // 何が足りないのか分かるようにフィールドの名前が含まれる。
            Assert.Contains("token", error.Message)
            // 別の応答の読み込みで止まっても上の確かめは通るので、サインインまで進んだことを見る。
            Assert.Equal(1, server.CountOf("POST", "/api/v1/auths/signin"))
            Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))
        })

[<Fact>]
let ``存在確認のGETがサーバエラーを返すとSyncErrorで止まり作成へ進まない`` () : Task =
    // サーバエラーを未登録とみなして作成へ進むと、意図しない作成につながる。
    let routes =
        modelRoutes
        |> Map.add ("GET", "/api/v1/models/model") (500, """{"detail":"internal error"}""")

    withFixedServer routes (fun server ->
        task {
            // 失敗の理由が調査できるようにステータスコードが含まれる。
            do!
                assertSyncError
                    [ "yuukaの存在確認に失敗"; "500" ]
                    (makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]))

            Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))
        })

[<Fact>]
let ``同じidのModel定義が複数あるとSyncErrorで止まる`` () : Task =
    withFixedServer modelRoutes (fun server ->
        task {
            let directory = makeModelsDirectory [ makeForm "yuuka" ]
            // 別のファイル名で同じidの定義を置く。
            File.WriteAllText(
                Path.Combine(directory, "duplicated.json"),
                OpenWebui.toJson (makeForm "yuuka")
            )

            // どの定義が衝突したのか調査できるようにidが含まれる。
            do! assertSyncError [ "Modelのidが重複"; "yuuka" ] (makeOptions server directory)
            // 後勝ちの上書きが起きないように、登録先へ触れる前に止まる。
            Assert.Empty(server.Received())
        })

[<Fact>]
let ``同じ名前のKnowledgeコレクションが複数あるとSyncErrorで止まる`` () : Task =
    withFixedServer knowledgeRoutes (fun server ->
        task {
            let directory =
                makeKnowledgeDirectory [ "character-appellation", [ "a.md", "呼称A" ] ]

            // 別のディレクトリ名で同じ名前の定義を置く。
            let duplicated = Path.Combine(directory, "duplicated")

            Directory.CreateDirectory(
                Path.Combine(duplicated, OpenWebuiKnowledge.filesDirectoryName)
            )
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

            do! assertSyncError [ "名前が重複"; "character-appellation" ] options
            Assert.Empty(server.Received())
        })

[<Fact>]
let ``紐付け先のKnowledgeが同期の対象に無いとSyncErrorで止まる`` () : Task =
    withFixedServer knowledgeRoutes (fun server ->
        task {
            let models = makeModelsDirectory [ makeFormWithKnowledge "yuuka" [ "typo-name" ] ]

            let options =
                { makeOptions server models with
                    KnowledgeDirectory = Some(makeAppellationDirectory ()) }

            // 綴りの誤りに気付けるように参照先の名前が含まれる。
            do! assertSyncError [ "同期の対象にありません"; "typo-name" ] options
            // 参照が外れたModelを黙って登録しない。
            Assert.Equal(0, server.CountOf("POST", "/api/v1/models/create"))
        })

[<Fact>]
let ``アップロードの応答にidが無いと止まる`` () : Task =
    // 応答の形が変わった状況を模す。
    // idのキーが無い応答は読み込みの時点でJsonExceptionで止まる。
    // キーがあって値が空の応答は読み込みを通ってしまうので、別のテストが受け持つ。
    let routes =
        knowledgeRoutes
        |> Map.add ("POST", "/api/v1/files/") (200, """{"detail":"ok"}""")

    withFixedServer routes (fun server ->
        task {
            let options =
                { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
                    KnowledgeDirectory = Some(makeAppellationDirectory ()) }

            let! error = Assert.ThrowsAsync<JsonException>(fun () -> sync options :> Task)

            // 何が足りないのか分かるようにフィールドの名前が含まれる。
            Assert.Contains("id", error.Message)
            // `id`は`invalid`のようなJSONの一般的なエラーメッセージにも含まれる。
            // アップロードより前の応答で止まっても上の確かめは通るので、アップロードまで進んだことを見る。
            Assert.Equal(1, server.CountOf("POST", "/api/v1/files/"))
            Assert.Equal(0, server.CountOf("POST", "/api/v1/knowledge/collection-id/file/add"))
        })

// 打ち切りが壊れると同じページを辿り続けて返らなくなるため、時間を区切る。
[<Fact(Timeout = 30000)>]
let ``総件数に届かなくても空のページが返れば一覧の取得を打ち切る`` () : Task =
    // 実物では起こしにくい、総件数と中身が食い違った応答を模す。
    // このサーバはクエリ文字列を見ないので、どのページを求めても空のページが返る。
    let routes =
        knowledgeRoutes
        |> Map.add
            ("GET", "/api/v1/knowledge/collection-id/files")
            (200, """{"items":[],"total":5}""")

    withFixedServer routes (fun server ->
        task {
            let options =
                { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
                    KnowledgeDirectory = Some(makeAppellationDirectory ()) }

            do! sync options

            Assert.Equal(1, server.CountOf("GET", "/api/v1/knowledge/collection-id/files"))
        })

[<Fact>]
let ``アップロードの応答のidが空だとSyncErrorで止まる`` () : Task =
    // キーがあっても値が空なら、読み込みは通ってしまう。
    // そのまま進むと空のfile_idでコレクションへの追加が飛ぶ。
    let routes =
        knowledgeRoutes |> Map.add ("POST", "/api/v1/files/") (200, """{"id":""}""")

    withFixedServer routes (fun server ->
        task {
            let options =
                { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
                    KnowledgeDirectory = Some(makeAppellationDirectory ()) }

            do! assertSyncError [ "/api/v1/files/の応答のidが空です" ] options
            Assert.Equal(0, server.CountOf("POST", "/api/v1/knowledge/collection-id/file/add"))
        })

/// コレクションのファイル一覧への要求の数。
/// 空のidで組み立てたURLはパスの形が崩れるので、末尾だけで数える。
let private filesRequestCount (server: FixedServer) : int =
    server.Received()
    |> List.filter (fun request -> request.Path.EndsWith("/files", StringComparison.Ordinal))
    |> List.length

[<Fact>]
let ``コレクションの作成の応答のidが空だとSyncErrorで止まる`` () : Task =
    // 空のidのまま進むと、`/api/v1/knowledge//files`のような壊れたURLへ要求が飛ぶ。
    let routes =
        knowledgeRoutes
        |> Map.add
            ("POST", "/api/v1/knowledge/create")
            (200, """{"id":"","name":"character-appellation","description":"説明"}""")

    withFixedServer routes (fun server ->
        task {
            let options =
                { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
                    KnowledgeDirectory = Some(makeAppellationDirectory ()) }

            do! assertSyncError [ "/api/v1/knowledge/createの応答のidが空です" ] options
            Assert.Equal(0, filesRequestCount server)
        })

[<Fact>]
let ``登録済みのコレクションのidが空だとSyncErrorで止まる`` () : Task =
    let collection = """{"id":"","name":"character-appellation","description":"説明"}"""

    let routes =
        knowledgeRoutes
        |> Map.add
            ("GET", "/api/v1/knowledge/")
            (200, $$"""{"items":[%%s{{collection}}],"total":1}""")

    withFixedServer routes (fun server ->
        task {
            let options =
                { makeOptions server (makeModelsDirectory [ makeForm "yuuka" ]) with
                    KnowledgeDirectory = Some(makeAppellationDirectory ()) }

            do! assertSyncError [ "/api/v1/knowledge/の応答のidが空です" ] options
            Assert.Equal(0, filesRequestCount server)
        })
