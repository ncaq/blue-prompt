/// スキルから生成したModelFormのJSON群をOpen WebUIのインスタンスへ同期する。
/// Open WebUIのModelはDBに保存される状態なので、
/// APIで登録済みのModelと突き合わせて、
/// 無ければ作成し、スキルの改良などで差分があれば上書きし、
/// 差分が無ければ書き込まない。
/// 繰り返し実行しても結果は同じになる。
module BluePrompt.OpenWebuiSync

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

/// 同期先のインスタンスに依存していてこちらからは知り得ない接続情報。
type Options =
    { ModelsDirectory: string
      Url: string
      BaseModelId: string option
      ApiKeyFile: string option
      KnowledgeDirectory: string option
      RagTemplateFile: string option }

/// 引数の解釈や同期先との通信に失敗した時の理由。
exception SyncError of message: string

let rec private parseFlags (options: Options) (flags: string list) : Options =
    match flags with
    | [] -> options
    | "--base-model-id" :: value :: rest ->
        parseFlags
            { options with
                BaseModelId = Some value }
            rest
    | "--api-key-file" :: value :: rest -> parseFlags { options with ApiKeyFile = Some value } rest
    | "--knowledge" :: value :: rest ->
        parseFlags
            { options with
                KnowledgeDirectory = Some value }
            rest
    | "--rag-template-file" :: value :: rest ->
        parseFlags
            { options with
                RagTemplateFile = Some value }
            rest
    | flag :: _ -> raise (SyncError $"解釈できない引数です: %s{flag}")

/// コマンドライン引数からOptionsを組み立てる。
/// 先頭2つはモデル定義のディレクトリとベースURLの位置引数で、
/// 残りは省略可能なフラグとして解釈する。
let parseOptions (args: string list) : Options =
    match args with
    | modelsDirectory :: url :: flags ->
        parseFlags
            { ModelsDirectory = modelsDirectory
              // パスの連結を単純にするため末尾スラッシュは落とす。
              Url = url.TrimEnd '/'
              BaseModelId = None
              ApiKeyFile = None
              KnowledgeDirectory = None
              RagTemplateFile = None }
            flags
    | _ -> raise (SyncError "モデル定義のディレクトリとベースURLを指定してください")

/// socket activationで初回アクセス時に起動する構成でも同期できるように、
/// 接続エラーも含めてリトライしながらインスタンスの起動を待つ。
let private waitForHealth (client: HttpClient) (url: string) : Task<unit> =
    let maxAttempts = 30

    let rec attemptFrom (attempt: int) : Task<unit> =
        task {
            let! healthy =
                task {
                    // 接続が確立しない環境ではHttpClientの既定タイムアウト(100秒)まで、
                    // 1試行がブロックし得るため、試行ごとに短い打ち切りを設ける。
                    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

                    try
                        // 応答は読まずに捨てるため、接続をすぐプールへ返せるように破棄する。
                        use! response = client.GetAsync($"%s{url}/health", timeout.Token)
                        return response.IsSuccessStatusCode
                    with
                    | :? HttpRequestException -> return false
                    // 打ち切りはTaskCanceledExceptionとして届くためリトライ対象へ含める。
                    | :? OperationCanceledException -> return false
                }

            if healthy then
                return ()
            elif maxAttempts <= attempt then
                return raise (SyncError $"%s{url}の起動を待ちましたが応答がありません")
            else
                do! Task.Delay(TimeSpan.FromSeconds 2.0)
                return! attemptFrom (attempt + 1)
        }

    attemptFrom 1

/// 認証を無効化したインスタンスがadminへ割り当てる資格情報。
/// backend/open_webui/routers/auths.pyのsigninが`WEBUI_AUTH`無効時に使う定数で、
/// 秘密ではなくOpen WebUIのソースに書かれているものをそのまま持ってきている。
let private noAuthCredentials = """{"email":"admin@localhost","password":"admin"}"""

/// 認証を無効化したインスタンスのadminとしてサインインし、Bearerトークンを得る。
/// `WEBUI_AUTH`を無効にしてもAPIはトークンを要求するため、
/// APIキーを渡さない運用ではUIが裏で行っているのと同じサインインを自分で行う。
let private signIn (client: HttpClient) (url: string) : Task<string> =
    task {
        use content =
            new StringContent(noAuthCredentials, Encoding.UTF8, "application/json")

        use! response = client.PostAsync($"%s{url}/api/v1/auths/signin", content)
        let! body = response.Content.ReadAsStringAsync()

        if not response.IsSuccessStatusCode then
            // 認証を有効にしたインスタンスではこの資格情報が通らないため、
            // APIキーの指定が要ることに気付けるようにステータスと本文を残す。
            raise (SyncError $"%s{url}へのサインインに失敗しました: %d{int response.StatusCode} %s{body}")

        match JsonNode.Parse body with
        | null -> return raise (SyncError $"サインインの応答を解釈できません: %s{body}")
        | root ->
            match root["token"] with
            | null -> return raise (SyncError $"サインインの応答にtokenがありません: %s{body}")
            | token -> return token.GetValue<string>()
    }

/// 要求を送って応答のJSONを返す。
/// 失敗した時はどのURLがどう失敗したのか分かる形でSyncErrorにする。
let private send (client: HttpClient) (request: HttpRequestMessage) : Task<JsonNode> =
    task {
        let url = request.RequestUri
        use! response = client.SendAsync request
        let! body = response.Content.ReadAsStringAsync()

        if not response.IsSuccessStatusCode then
            raise (SyncError $"%O{url}への要求が失敗しました: %d{int response.StatusCode} %s{body}")

        match JsonNode.Parse body with
        | null -> return raise (SyncError $"%O{url}の応答を解釈できません: %s{body}")
        | root -> return root
    }

/// JSONを返すAPIをGETで呼ぶ。
let private getJson (client: HttpClient) (url: string) : Task<JsonNode> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, url)
        return! send client request
    }

/// JSONのボディをPOSTする。
let private postJson (client: HttpClient) (url: string) (body: JsonNode) : Task<JsonNode> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, url)
        request.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        return! send client request
    }

/// 文字列1つだけを持つJSONオブジェクトを組み立てる。
let private jsonObjectOf (key: string) (value: string) : JsonNode =
    let node = JsonObject()
    node[key] <- JsonValue.Create value
    node

/// ModelFormをJSONボディとしてPOSTする。
let private postForm (client: HttpClient) (url: string) (form: OpenWebui.ModelForm) : Task<unit> =
    task {
        use content =
            new StringContent(OpenWebui.toJson form, Encoding.UTF8, "application/json")

        use! response = client.PostAsync(url, content)

        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync()
            raise (SyncError $"%s{url}への書き込みに失敗しました: %d{int response.StatusCode} %s{body}")
    }

/// Knowledgeコレクション1つ分の、同期したい内容。
type private DesiredKnowledge =
    {
        Form: OpenWebuiKnowledge.KnowledgeForm
        /// 登録するファイルの名前と中身。
        /// 中身をバイト列で持つのは、
        /// Open WebUIが差分の判定に使うハッシュが生バイト列に対するものだから。
        Files: (string * byte array) list
    }

/// open-webui-knowledgeの生成物を読み込む。
/// 出力ディレクトリの直下にコレクションごとのディレクトリが並ぶ。
let private readKnowledgeDirectory (directory: string) : DesiredKnowledge list =
    Directory.GetDirectories directory
    |> Array.sort
    |> Array.toList
    |> List.map (fun collectionDirectory ->
        let formPath = Path.Combine(collectionDirectory, OpenWebuiKnowledge.formFileName)

        if not (File.Exists formPath) then
            raise (SyncError $"%s{formPath}がありません")

        let filesDirectory =
            Path.Combine(collectionDirectory, OpenWebuiKnowledge.filesDirectoryName)

        { Form = OpenWebuiKnowledge.ofJson (File.ReadAllText formPath)
          Files =
            Directory.GetFiles filesDirectory
            |> Array.sort
            |> Array.toList
            |> List.map (fun path -> Path.GetFileName path, File.ReadAllBytes path) })

/// Open WebUIがアップロード時にmeta.file_hashへ入れる、生バイト列のSHA-256。
/// backend/open_webui/routers/files.pyが`hashlib.sha256(contents).hexdigest()`で計算しており、
/// 差分の判定に使えるように同じ値をこちらでも求める。
/// トップレベルのhashは抽出後のテキストに対するもので手元からは再現できないため使わない。
let private sha256Hex (bytes: byte array) : string =
    Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData bytes)

/// 一覧のAPIが配列を返す場合と、itemsを持つオブジェクトを返す場合の両方から項目を取り出す。
/// Open WebUIは版によってこの形が変わるため、どちらでも読めるようにする。
let private listItems (url: string) (root: JsonNode) : JsonNode list =
    match root with
    | :? JsonArray as items -> List.ofSeq items
    | _ ->
        match root["items"] with
        | :? JsonArray as items -> List.ofSeq items
        | _ -> raise (SyncError $"%s{url}の応答を一覧として解釈できません: %s{root.ToJsonString()}")

/// 一覧APIが応答へ載せる総件数。オブジェクトを返さない旧い形では無い。
let private totalOf (root: JsonNode) : int option =
    match root with
    | :? JsonObject ->
        match root["total"] with
        | null -> None
        | total -> Some(total.GetValue<int>())
    | _ -> None

/// 一覧APIを最後のページまで辿って全ての項目を集める。
/// Knowledgeのファイル一覧は既定では30件しか返らないため、
/// 1ページだけを見ると登録済みのファイルを未登録と取り違えて、
/// 同じ内容を登録し直そうとしてDuplicate contentで弾かれる。
let private getAllItems (client: HttpClient) (url: string) : Task<JsonNode list> =
    let rec fromPage (page: int) (collected: JsonNode list) : Task<JsonNode list> =
        task {
            let pageUrl = $"%s{url}?page=%d{page}"
            let! response = getJson client pageUrl
            let items = listItems pageUrl response
            let accumulated = collected @ items

            // 総件数を返さない形の応答では1ページで全件とみなす。
            // 進まないページを延々と辿らないように、
            // 空のページが返った時点でも打ち切る。
            let finished =
                List.isEmpty items
                || (match totalOf response with
                    | None -> true
                    | Some total -> total <= List.length accumulated)

            if finished then
                return accumulated
            else
                return! fromPage (page + 1) accumulated
        }

    fromPage 1 []

/// JSONの文字列フィールドを読む。無ければ空文字として扱う。
let private stringField (node: JsonNode) (keys: string list) : string =
    let rec dig (current: JsonNode) (keys: string list) =
        match current, keys with
        | null, _ -> None
        | _, [] -> Some current
        | _, key :: rest -> dig current[key] rest

    match dig node keys with
    | None -> ""
    | Some value -> value.GetValue<string>()

/// 後続の処理が成立しなくなるJSONの文字列フィールドを読む。
/// 空のまま進むとURLの組み立てやPOSTのボディが壊れた形になり、
/// 原因から遠い場所で失敗するため、応答の形が変わった時点で気付けるように止める。
let private requiredStringField (url: string) (node: JsonNode) (keys: string list) : string =
    match stringField node keys with
    | "" ->
        let path = String.concat "." keys
        raise (SyncError $"%s{url}の応答に%s{path}がありません: %s{node.ToJsonString()}")
    | value -> value

/// ファイルをアップロードしてfile idを得る。
/// 既定ではアップロードの後処理がバックグラウンドへ回されて、
/// 続くコレクションへの追加が未処理のファイルとして弾かれるため、
/// process_in_backgroundを無効にして抽出まで終わらせてから戻る。
let private uploadFile
    (client: HttpClient)
    (url: string)
    (fileName: string)
    (content: byte array)
    : Task<string> =
    task {
        use request =
            new HttpRequestMessage(
                HttpMethod.Post,
                $"%s{url}/api/v1/files/?process_in_background=false"
            )

        let form = new MultipartFormDataContent()
        let file = new ByteArrayContent(content)
        file.Headers.ContentType <- MediaTypeHeaderValue "text/markdown"
        form.Add(file, "file", fileName)
        request.Content <- form

        let! response = send client request
        return requiredStringField $"%s{url}/api/v1/files/" response [ "id" ]
    }

/// Knowledgeコレクション1つを同期して、そのidを返す。
/// 登録済みのファイルとは名前で対応付け、
/// 中身が変わっていないファイルには触らない。
///
/// 説明の更新へ送るボディはKnowledgeFormのnameとdescriptionだけで、
/// Modelの更新と違ってaccess_grantsを載せる必要はない。
/// KnowledgeFormのaccess_grantsは`Optional[list[dict]] = None`で、
/// backend/open_webui/models/knowledge.pyのupdate_knowledge_by_idが、
/// `if form_data.access_grants is not None`の時だけ書き込むため、
/// 省けば登録済みの権限の設定はそのまま残る。
/// 更新のルータもフォームを組み直さないので、
/// Modelの更新で踏んだ検証の失敗も起きない。
let private syncKnowledge
    (client: HttpClient)
    (url: string)
    (existing: JsonNode option)
    (desired: DesiredKnowledge)
    : Task<string> =
    task {
        let name = desired.Form.Name

        let! id =
            task {
                match existing with
                | Some collection ->
                    let id = requiredStringField $"%s{url}/api/v1/knowledge/" collection [ "id" ]

                    if stringField collection [ "description" ] <> desired.Form.Description then
                        let! _ =
                            postJson
                                client
                                $"%s{url}/api/v1/knowledge/%s{id}/update"
                                (JsonNode.Parse(OpenWebuiKnowledge.toJson desired.Form))

                        printfn $"%s{name}: 説明を更新"

                    return id
                | None ->
                    let! created =
                        postJson
                            client
                            $"%s{url}/api/v1/knowledge/create"
                            (JsonNode.Parse(OpenWebuiKnowledge.toJson desired.Form))

                    printfn $"%s{name}: 作成"
                    return requiredStringField $"%s{url}/api/v1/knowledge/create" created [ "id" ]
            }

        // コレクション単体を取るAPIのfilesはnullで返るため、
        // 登録済みのファイルは専用の一覧APIから取る。
        let! files = getAllItems client $"%s{url}/api/v1/knowledge/%s{id}/files"

        // 登録済みのファイルを名前で引けるようにする。
        // 同じ名前が複数あるのは以前の同期が中断した場合などの壊れた状態なので、
        // 1つを残して他は登録し直しの対象にする。
        let registered =
            files
            |> List.map (fun file ->
                stringField file [ "meta"; "name" ],
                (stringField file [ "id" ], stringField file [ "meta"; "file_hash" ]))

        let registeredByName = Map.ofList registered
        let desiredNames = desired.Files |> List.map fst |> Set.ofList

        let removeFile (fileId: string) =
            task {
                let! _ =
                    postJson
                        client
                        $"%s{url}/api/v1/knowledge/%s{id}/file/remove"
                        (jsonObjectOf "file_id" fileId)

                return ()
            }

        let addFile (fileName: string) (content: byte array) =
            task {
                let! fileId = uploadFile client url fileName content

                let! _ =
                    postJson
                        client
                        $"%s{url}/api/v1/knowledge/%s{id}/file/add"
                        (jsonObjectOf "file_id" fileId)

                return ()
            }

        // 生成物から消えたファイルと、名前が重複した余りを取り除く。
        let staleIds =
            registered
            |> List.choose (fun (fileName, (fileId, _)) ->
                // 名前で引いた時に選ばれなかった同名のファイルは、
                // 以降の比較の対象にならないまま残り続けるため取り除く。
                let chosen =
                    match Map.tryFind fileName registeredByName with
                    | Some(chosenId, _) -> chosenId = fileId
                    | None -> false

                if not (Set.contains fileName desiredNames) || not chosen then
                    Some fileId
                else
                    None)

        for fileId in staleIds do
            do! removeFile fileId

        // 中身が変わったファイルを、登録済みのidと組にして選び出す。
        let updates =
            desired.Files
            |> List.choose (fun (fileName, content) ->
                match Map.tryFind fileName registeredByName with
                | Some(fileId, hash) when hash <> sha256Hex content ->
                    Some(fileId, fileName, content)
                | _ -> None)

        // 登録済みに無いファイルを選び出す。
        let additions =
            desired.Files
            |> List.filter (fun (fileName, _) -> not (Map.containsKey fileName registeredByName))

        for fileId, fileName, content in updates do
            // 中身が変わったファイルは、
            // 登録し直すことで古い埋め込みが残らないようにする。
            do! removeFile fileId
            do! addFile fileName content

        for fileName, content in additions do
            do! addFile fileName content

        let removed = List.length staleIds
        let updated = List.length updates
        let added = List.length additions

        if added = 0 && updated = 0 && removed = 0 then
            printfn $"%s{name}: ファイルの変更なし"
        else
            printfn $"%s{name}: ファイルを追加%d{added}件、更新%d{updated}件、削除%d{removed}件"

        return id
    }

/// Knowledgeコレクションを先頭から順に同期して、名前からidへの対応を組み立てる。
/// 採番されたidは次のコレクションの同期へ持ち越す状態なので、畳み込みの状態として持ち回る。
/// 状態を`Task`にして各段が前の段を待つことで、畳み込みのまま逐次に同期する。
let private syncKnowledgeCollections
    (client: HttpClient)
    (url: string)
    (existingByName: Map<string, JsonNode>)
    (knowledge: DesiredKnowledge list)
    : Task<Map<string, string>> =
    knowledge
    |> List.fold
        (fun (previous: Task<Map<string, string>>) desired ->
            task {
                let! ids = previous
                let existing = Map.tryFind desired.Form.Name existingByName
                let! id = syncKnowledge client url existing desired
                return Map.add desired.Form.Name id ids
            })
        (Task.FromResult Map.empty)

/// Modelのmeta.knowledgeへ、同期したコレクションのidを埋める。
/// 生成の時点ではインスタンスが採番するidが分からないため、
/// 名前で引き当ててここで解決する。
let private resolveKnowledge
    (ids: Map<string, string>)
    (form: OpenWebui.ModelForm)
    : OpenWebui.ModelForm =
    match form.Meta.Knowledge with
    | None -> form
    | Some references ->
        let resolved =
            references
            |> List.map (fun reference ->
                match Map.tryFind reference.Name ids with
                | Some id -> { reference with Id = Some id }
                | None ->
                    // 紐付け先の定義が同期の対象に無ければ、
                    // 参照が外れたModelを黙って登録せず、綴りの誤りに気付けるように止める。
                    raise (
                        SyncError $"%s{form.Id}が参照するKnowledgeコレクション%s{reference.Name}が同期の対象にありません"
                    ))

        { form with
            Meta =
                { form.Meta with
                    Knowledge = Some resolved } }

/// インスタンス全体のRAGプロンプトテンプレートを同期する。
/// Modelへ紐付けたKnowledgeが自動で注入される時、
/// このテンプレートがシステムプロンプトの後ろへ連結されるため、
/// 既定の引用指示のままではロールプレイの人格が壊れる。
///
/// 送るのはRAG_TEMPLATEのキー1つだけだが、
/// これはインスタンス全体のRAG設定を扱う管理者APIなので部分更新かどうかを確かめてある。
/// backend/open_webui/routers/retrieval.pyのupdate_rag_configは、
/// 全てのフィールドを`form_data.X if form_data.X is not None else config.X`で当てていて、
/// 送らなかった埋め込みモデルやWeb検索の設定は現在の値のまま残る。
let private syncRagTemplate (client: HttpClient) (url: string) (template: string) : Task<unit> =
    task {
        let! config = getJson client $"%s{url}/api/v1/retrieval/config"

        if stringField config [ "RAG_TEMPLATE" ] = template then
            printfn "RAGテンプレート: 変更なし"
        else
            let! _ =
                postJson
                    client
                    $"%s{url}/api/v1/retrieval/config/update"
                    (jsonObjectOf "RAG_TEMPLATE" template)

            printfn "RAGテンプレート: 更新"
    }

/// 更新のボディへ、登録済みのModelのaccess_grantsをそのまま載せる。
///
/// 中身は登録先の利用者やグループの構成に依存していてこのリポジトリからは決められないため、
/// 管理対象のフィールドとしては持たず、
/// 読み取った値をそのまま送り返して権限の設定を保つ。
///
/// 更新のAPIはこのフィールドを省いても null を入れても500を返す。
/// backend/open_webui/routers/models.pyのupdate_model_by_idが、
/// 最後に`ModelForm(**form_data.model_dump())`で組み直すためで、
/// ModelFormのaccess_grantsは`list[dict | None] = None`とNoneを許さない型なので、
/// 既定のNoneのまま検証へ入ると落ちる。
/// つまり配列を送るしか選択肢が無い。
///
/// 応答のaccess_grantsが欠けている場合は、
/// 空の配列を作って権限を消してしまわないように止める。
/// ModelModelのaccess_grantsは`Field(default_factory=list)`で、
/// _to_model_modelが必ずDBから埋めて返すため、
/// 欠けているのは応答の形がこちらの想定と違うということであり、
/// 推測で書き込んでよい状況ではない。
let private withAccessGrants (current: JsonNode) (form: OpenWebui.ModelForm) : JsonNode =
    match JsonNode.Parse(OpenWebui.toJson form) with
    | null -> raise (SyncError $"%s{form.Id}の更新のボディを組み立てられません")
    | body ->
        body["access_grants"] <-
            match current["access_grants"] with
            | null -> raise (SyncError $"%s{form.Id}の応答にaccess_grantsがありません")
            | grants -> grants.DeepClone()

        body

/// ModelForm1件を同期する。
let private syncModel
    (client: HttpClient)
    (options: Options)
    (desired: OpenWebui.ModelForm)
    : Task<unit> =
    task {
        let id = desired.Id

        use! response =
            client.GetAsync $"%s{options.Url}/api/v1/models/model?id=%s{Uri.EscapeDataString id}"

        if response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync()

            // 更新のボディを組み立てる時にも応答を読むため、パースは1回で済ませる。
            match JsonNode.Parse body with
            | null -> raise (SyncError $"%s{id}の応答を解釈できません: %s{body}")
            | currentNode ->
                // 応答を管理対象のフィールドだけへ正規化して比較する。
                let current = OpenWebui.ofJsonNode id currentNode

                // base-model-idを指定しない運用では、
                // 登録後にUIで選ばれた上流モデルを上書きせず保持する。
                let desired =
                    match options.BaseModelId with
                    | Some _ -> desired
                    | None ->
                        { desired with
                            BaseModelId = current.BaseModelId }

                if current = desired then
                    printfn $"%s{id}: 変更なし"
                else
                    let updateUrl =
                        $"%s{options.Url}/api/v1/models/model/update?id=%s{Uri.EscapeDataString id}"

                    let! _ = postJson client updateUrl (withAccessGrants currentNode desired)
                    printfn $"%s{id}: 更新"
        elif
            // Open WebUIは未登録のidへ401を返す。404も未登録相当として扱う。
            response.StatusCode = HttpStatusCode.Unauthorized
            || response.StatusCode = HttpStatusCode.NotFound
        then
            do! postForm client $"%s{options.Url}/api/v1/models/create" desired
            printfn $"%s{id}: 作成"
        else
            // サーバエラーやプロキシエラーを未登録とみなして作成へ進むと、
            // 真因の分かりにくい失敗や意図しない作成につながるため理由を明示して止める。
            let! body = response.Content.ReadAsStringAsync()
            raise (SyncError $"%s{id}の存在確認に失敗しました: %d{int response.StatusCode} %s{body}")
    }

/// ディレクトリ内の全ModelFormのJSONを同期する。
let sync (options: Options) : Task<unit> =
    task {
        use client = new HttpClient()

        // 読み取りは起動待ちより前に済ませて、
        // ファイルが無い時に接続の待ち時間を費やしてから失敗しないようにする。
        let! configuredApiKey =
            task {
                match options.ApiKeyFile with
                | Some path ->
                    let! apiKey = File.ReadAllTextAsync path
                    return Some(apiKey.Trim())
                | None -> return None
            }

        // 生成物のbase_model_idはnullなので、指定があれば流し込む。
        let withBaseModelId (form: OpenWebui.ModelForm) =
            match options.BaseModelId with
            | Some _ ->
                { form with
                    BaseModelId = options.BaseModelId }
            | None -> form

        let! loadedForms =
            Directory.GetFiles(options.ModelsDirectory, "*.json")
            |> Array.sort
            |> Array.map (fun path ->
                task {
                    let! json = File.ReadAllTextAsync path
                    return path, withBaseModelId (OpenWebui.ofJson path json)
                })
            |> Task.WhenAll

        let forms = List.ofArray loadedForms

        let knowledge =
            match options.KnowledgeDirectory with
            | Some directory -> readKnowledgeDirectory directory
            | None -> []

        let! ragTemplate =
            task {
                match options.RagTemplateFile with
                | Some path ->
                    let! template = File.ReadAllTextAsync path
                    return Some template
                | None -> return None
            }

        // idが重複すると同じModelへ交互に上書きする後勝ちになり、
        // どのスキルが反映されたのか分からなくなるため、同期の前に検出して止める。
        for id, group in List.groupBy (fun (_, form: OpenWebui.ModelForm) -> form.Id) forms do
            if 1 < List.length group then
                let fileNames = group |> List.map (fst >> Path.GetFileName) |> String.concat ", "
                raise (SyncError $"Modelのidが重複しています: %s{id} (%s{fileNames})")

        // Knowledgeも名前で引き当てて同期するため、
        // 重複していると同じコレクションへ交互に書き込むことになる。
        let knowledgeName (item: DesiredKnowledge) = item.Form.Name

        for name, group in List.groupBy knowledgeName knowledge do
            if 1 < List.length group then
                raise (SyncError $"Knowledgeコレクションの名前が重複しています: %s{name}")

        do! waitForHealth client options.Url

        // サインインは起動後でなければ受け付けられないため、起動を待ってから行う。
        let! token =
            task {
                match configuredApiKey with
                | Some apiKey -> return apiKey
                | None -> return! signIn client options.Url
            }

        client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

        match ragTemplate with
        | Some template -> do! syncRagTemplate client options.Url template
        | None -> ()

        // Modelの紐付けにはコレクションのidが要るため、Knowledgeを先に同期する。
        // 一覧はコレクションごとに取り直さず1度だけ取る。
        // 往復が減るだけでなく、
        // 同期の途中で一覧が変わっても引き当ての基準がぶれない。
        let! existingCollections =
            task {
                match knowledge with
                | [] -> return []
                | _ -> return! getAllItems client $"%s{options.Url}/api/v1/knowledge/"
            }

        let existingByName =
            existingCollections
            |> List.map (fun collection -> stringField collection [ "name" ], collection)
            |> Map.ofList

        let! knowledgeIds = syncKnowledgeCollections client options.Url existingByName knowledge

        for _, desired in forms do
            do! syncModel client options (resolveKnowledge knowledgeIds desired)
    }
