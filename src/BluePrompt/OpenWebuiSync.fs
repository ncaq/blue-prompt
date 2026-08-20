/// スキルから生成したModelFormのJSON群をOpen WebUIのインスタンスへ同期する。
/// Open WebUIのModelはDBに保存される状態なので、
/// APIで登録済みのModelと突き合わせて、
/// 無ければ作成し、スキルの改良などで差分があれば上書きし、
/// 差分が無ければ書き込まない。
/// 繰り返し実行しても結果は同じになる。
module BluePrompt.OpenWebuiSync

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks

/// 同期先のインスタンスに依存していてこちらからは知り得ない接続情報。
type Options =
    { ModelsDirectory: string
      Url: string
      BaseModelId: string option
      ApiKeyFile: string option }

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
              ApiKeyFile = None }
            flags
    | _ -> raise (SyncError "モデル定義のディレクトリとベースURLを指定してください")

/// socket activationで初回アクセス時に起動する構成でも同期できるように、
/// 接続エラーも含めてリトライしながらインスタンスの起動を待つ。
let private waitForHealth (client: HttpClient) (url: string) : Task<unit> =
    task {
        let maxAttempts = 30
        let mutable attempt = 1
        let mutable ready = false

        while not ready do
            let! healthy =
                task {
                    try
                        let! response = client.GetAsync $"%s{url}/health"
                        return response.IsSuccessStatusCode
                    with :? HttpRequestException ->
                        return false
                }

            if healthy then
                ready <- true
            elif maxAttempts <= attempt then
                raise (SyncError $"%s{url}の起動を待ちましたが応答がありません")
            else
                attempt <- attempt + 1
                do! Task.Delay(TimeSpan.FromSeconds 2.0)
    }

/// ModelFormをJSONボディとしてPOSTする。
let private postForm (client: HttpClient) (url: string) (form: OpenWebui.ModelForm) : Task<unit> =
    task {
        use content =
            new StringContent(OpenWebui.toJson form, Encoding.UTF8, "application/json")

        let! response = client.PostAsync(url, content)

        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync()
            raise (SyncError $"%s{url}への書き込みに失敗しました: %d{int response.StatusCode} %s{body}")
    }

/// ModelFormのJSONファイル1つを同期する。
let private syncModel (client: HttpClient) (options: Options) (path: string) : Task<unit> =
    task {
        let! json = File.ReadAllTextAsync path

        let desired =
            let form = OpenWebui.ofJson json

            // 生成物のbase_model_idはnullなので、指定があれば流し込む。
            match options.BaseModelId with
            | Some _ ->
                { form with
                    BaseModelId = options.BaseModelId }
            | None -> form

        let id = desired.Id

        let! response =
            client.GetAsync $"%s{options.Url}/api/v1/models/model?id=%s{Uri.EscapeDataString id}"

        if response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync()
            // 応答を管理対象のフィールドだけへ正規化して比較する。
            let current = OpenWebui.ofJson body

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
                do!
                    postForm
                        client
                        $"%s{options.Url}/api/v1/models/model/update?id=%s{Uri.EscapeDataString id}"
                        desired

                printfn $"%s{id}: 更新"
        else
            do! postForm client $"%s{options.Url}/api/v1/models/create" desired
            printfn $"%s{id}: 作成"
    }

/// ディレクトリ内の全ModelFormのJSONを同期する。
let sync (options: Options) : Task<unit> =
    task {
        use client = new HttpClient()

        match options.ApiKeyFile with
        | Some path ->
            let! apiKey = File.ReadAllTextAsync path

            client.DefaultRequestHeaders.Authorization <-
                AuthenticationHeaderValue("Bearer", apiKey.Trim())
        | None -> ()

        do! waitForHealth client options.Url

        for path in Directory.GetFiles(options.ModelsDirectory, "*.json") |> Array.sort do
            do! syncModel client options path
    }
