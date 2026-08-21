/// スキルをOpen WebUIのワークスペースModel定義へ変換する。
/// Open WebUIにはClaude Codeのスキルのような、
/// 指示書と参照ファイルの組をオンデマンドで読み込む仕組みが無いため、
/// SKILL.mdと明示的にリンクされた参照ファイルをインライン化して、
/// システムプロンプトへ焼き込んだカスタムモデル(ModelForm)のJSONを生成する。
/// 生成したJSONはPOST /api/v1/models/createへそのまま渡して登録できる。
module BluePrompt.OpenWebui

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open System.Text.Encodings.Web
open System.Text.RegularExpressions
open System.Threading.Tasks

/// SKILL.mdがOpen WebUIへ変換できる形式ではなかった時の対象パスと理由。
exception SkillFormatError of path: string * message: string

/// Open WebUIのModelのmeta.knowledgeへ入れるKnowledgeコレクションの参照。
/// ModelMetaのknowledgeはlist[Any]で内容を検証しないが、
/// 実質の形式はフロントエンドのtoModelKnowledgeReferenceが決めていて、
/// id・name・typeなどのキーだけを持つオブジェクトの配列になる。
/// typeが`collection`の項目はファイルのアクセス権検証の対象外なので、
/// この3つだけを送れば紐付けとして成立する。
///
/// idはコレクションを作った先のインスタンスが採番するため生成時には決まらない。
/// 生成物ではNoneのままにして、
/// open-webui-syncが名前でコレクションを引き当てて埋める。
type KnowledgeReference =
    { Id: string option
      Name: string
      Type: string }

/// KnowledgeReferenceのtypeへ入れる、コレクション全体を指す種別。
/// 単体ファイルを指す`file`と区別される。
let collectionType = "collection"

/// meta.knowledgeを毎リクエスト自動でRAG検索させるツール呼び出しの方式。
/// UIの表記では「Default」で、内部の値はこの`legacy`。
let legacyFunctionCalling = "legacy"

/// Open WebUIのModelFormのmetaフィールド。
/// backend/open_webui/models/models.pyのModelMetaに対応する。
/// descriptionはUIの一覧でモデルの説明として表示される。
/// knowledgeは参照させるKnowledgeコレクションで、紐付けが無いスキルではNone。
type Meta =
    { Description: string
      Knowledge: KnowledgeReference list option }

/// Open WebUIのModelFormのparamsフィールド。
/// ModelParamsはextra=allowの自由形式で、systemがシステムプロンプトになる。
///
/// functionCallingはツール呼び出しの方式で、UIの表記では`legacy`が「Default」。
/// backend/open_webui/utils/middleware.pyは、
/// この値が`legacy`の時だけmeta.knowledgeを自動でRAG検索してコンテキストへ入れる。
/// v0.10.0以降の既定は`native`で、その場合はモデルがビルトインツールを呼ばない限り、
/// 紐付けたKnowledgeが一切参照されないため、紐付けがあるModelでは明示的に指定する。
type Params =
    { System: string
      FunctionCalling: string option }

/// Open WebUIのワークスペースModelの作成フォーム。
/// backend/open_webui/models/models.pyのModelFormに対応する。
/// baseModelIdは実際に推論へ使う上流モデルで、
/// 登録先のインスタンスに依存するため生成時はnullにして登録後にUIで選ぶ。
type ModelForm =
    { Id: string
      BaseModelId: string option
      Name: string
      Meta: Meta
      Params: Params
      IsActive: bool }

/// SKILL.mdのYAMLフロントマター。
/// このリポジトリのスキルが使うのは1行のkey: valueだけなので、
/// YAMLパーサーを持ち込まずその形の行だけを解釈する。
///
/// Knowledgeは省略可能なknowledge:行で、
/// そのスキルがOpen WebUIで参照するKnowledgeコレクションの名前を並べる。
/// Claude Codeではスキルを会話の途中で読み込めるが、
/// Open WebUIのModelは会話の入口を選ぶだけで他のModelを読み込めないため、
/// 参照させたいナレッジをModelへ紐付ける必要がある。
type Frontmatter =
    { Name: string
      Description: string
      Knowledge: string list
      Body: string }

/// フロントマターの区切り行。
let private delimiter = "---"

/// フロントマターのkey: value行。
let private fieldPattern = Regex @"^([A-Za-z_-]+):\s*(.*)$"

/// SKILL.mdの本文からフロントマターの要素と、その後ろの本文を取り出す。
/// 必須の要素が欠けているなど形式が想定と異なる場合は、
/// 生成を黙って続けずSkillFormatErrorで止める。
/// 省略できる要素は、書かれていなければ空の値として扱う。
///
/// Modelを組み立てるOpenWebuiだけでなく、
/// Knowledgeを組み立てるOpenWebuiKnowledgeも同じ解釈を必要とするため公開している。
let parseFrontmatter (path: string) (content: string) : Frontmatter =
    let fail message = raise (SkillFormatError(path, message))

    match content.Replace("\r\n", "\n").Split '\n' |> Array.toList with
    | first :: rest when first = delimiter ->
        match List.tryFindIndex (fun line -> line = delimiter) rest with
        | None -> fail "フロントマターの閉じ---がありません"
        | Some closeIndex ->
            let fields =
                rest
                |> List.take closeIndex
                |> List.choose (fun line ->
                    match fieldPattern.Match line with
                    | m when m.Success -> Some(m.Groups[1].Value, m.Groups[2].Value)
                    | _ -> None)
                |> Map.ofList

            let field key =
                match Map.tryFind key fields with
                | Some value when value <> "" -> value
                | _ -> fail $"フロントマターに%s{key}がありません"

            // knowledge:は紐付けの無いスキルでは書かないため、
            // 欠けていることを失敗とせず空の一覧として扱う。
            let knowledge =
                match Map.tryFind "knowledge" fields with
                | None -> []
                | Some value ->
                    value.Split ','
                    |> Array.toList
                    |> List.choose (fun name ->
                        match name.Trim() with
                        | "" -> None
                        | trimmed -> Some trimmed)

            { Name = field "name"
              Description = field "description"
              Knowledge = knowledge
              Body = rest |> List.skip (closeIndex + 1) |> String.concat "\n" |> _.Trim() }
    | _ -> fail "フロントマターの開始---がありません"

/// Markdownのインラインリンクの参照先。
let private linkPattern = Regex @"\[[^\]]*\]\(([^)]+)\)"

/// URLのスキーム。`https:`のような形で参照ファイルのパスと区別する。
let private schemePattern = Regex @"^[A-Za-z][A-Za-z0-9+.-]*:"

/// 本文中で明示的にリンクされている参照ファイルの相対パスを、
/// 登場順の重複なしで列挙する。
/// スキーム付きのURLとページ内アンカーは参照ファイルではないので除外し、
/// それ以外のリンクは黙って無視せず全て参照ファイルとして扱う。
/// 表記ゆれの`./`は落として`./`有りと無しが同じファイルへまとまるようにする。
let localLinkTargets (body: string) : string list =
    linkPattern.Matches body
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> Seq.filter (fun target -> not (target.StartsWith '#') && not (schemePattern.IsMatch target))
    |> Seq.map (fun target ->
        if target.StartsWith("./", StringComparison.Ordinal) then
            target.Substring 2
        else
            target)
    |> Seq.distinct
    |> Seq.toList

/// 本文からリンクされた参照ファイルを、スキルディレクトリ内の実在するパスへ解決する。
/// 参照が壊れている場合は生成を黙って続けずSkillFormatErrorで止める。
/// 戻り値は本文に書かれた相対パスと実際のパスの組。
let resolveReferences
    (skillDirectory: string)
    (skillPath: string)
    (body: string)
    : (string * string) list =
    // 末尾の区切りの有無で前方一致の判定がぶれないように、区切りを1つ付けた形で持つ。
    let root =
        let fullPath = Path.GetFullPath skillDirectory

        if fullPath.EndsWith(Path.DirectorySeparatorChar) then
            fullPath
        else
            fullPath + string<char> Path.DirectorySeparatorChar

    localLinkTargets body
    |> List.map (fun fileName ->
        // 絶対パスやディレクトリを遡る参照はスキルディレクトリの外に出るため受け付けない。
        // 書かれた文字列の形ではなく、
        // 正規化した結果がスキルディレクトリの下にあるかどうかで判断する。
        // Path.Combineは絶対パスを渡されると連結せずそれ自体を返すため、
        // 文字列に`..`があるかどうかを見るだけでは絶対パスを弾けない。
        let filePath = Path.GetFullPath(Path.Combine(skillDirectory, fileName))

        if not (filePath.StartsWith(root, StringComparison.Ordinal)) then
            raise (SkillFormatError(skillPath, $"%s{fileName}はスキルディレクトリの外を参照しています"))

        if not (File.Exists filePath) then
            raise (SkillFormatError(skillPath, $"リンクされた%s{fileName}が存在しません"))

        fileName, filePath)

/// 内容のどこにあるバッククォート連続よりも長いコードフェンスを組み立てる。
/// 参照ファイル自身がコードフェンスを含んでいてもインライン化が壊れないようにする。
let private fenceFor (content: string) : string =
    let longestRun = Regex.Matches(content, "`+") |> Seq.map _.Length |> Seq.fold max 0

    String.replicate (max 3 (longestRun + 1)) "`"

/// 参照ファイル1つをインライン化した節へ組み立てる。
/// Markdownはそのまま埋め込み、それ以外は拡張子を言語タグにしたコードブロックで包む。
let private inlineSection (fileName: string) (content: string) : string =
    let heading = $"# 参照ファイル: %s{fileName}"

    let body =
        match (Path.GetExtension fileName).TrimStart '.' with
        | "md"
        | "markdown" -> content.Trim()
        | language ->
            let fence = fenceFor content
            $"%s{fence}%s{language}\n%s{content.Trim()}\n%s{fence}"

    $"%s{heading}\n\n%s{body}"

/// インライン化した参照ファイル群の前へ置く案内。
/// 本文が挙げている参照データとこの後ろの節を結び付ける。
let private inlineNotice = "以下は本文が挙げている参照データの中身です。"

/// スキルディレクトリのMODEL.md(無ければSKILL.md)と参照ファイルから
/// システムプロンプト全文を組み立てる。
/// リンクされたファイルが実在しない場合は参照が壊れているのでSkillFormatErrorで止める。
let private buildSystemPrompt (skillDirectory: string) (skillPath: string) (body: string) : string =
    let sections =
        resolveReferences skillDirectory skillPath body
        |> List.map (fun (fileName, filePath) -> inlineSection fileName (File.ReadAllText filePath))

    match sections with
    | [] -> body + "\n"
    | _ ->
        [ body; delimiter; inlineNotice ] @ sections
        |> String.concat "\n\n"
        |> fun prompt -> prompt + "\n"

/// スキルディレクトリからModelFormを組み立てる。
/// idとnameにはフロントマターのnameを使い、ここでは一意性を保証しない。
/// 同じ一覧の中のidの重複は、open-webui-syncが同期の前に検出して止める。
let buildModelForm (skillDirectory: string) : ModelForm =
    // Model向けの本文があればそちらを使う。
    // Claude Codeは参照ファイルを開いてナレッジのスキルを読み込むが、
    // Open WebUIでは参照ファイルはインライン化され、
    // ナレッジは紐付けから自動で渡されるため、本文の言い方が噛み合わない。
    // 両方の言い方を1つの本文へ収めると、
    // どちらの経路でも半分は当てはまらない説明を読ませることになる。
    let modelPath = Path.Combine(skillDirectory, SkillFile.model)
    let skillPath = Path.Combine(skillDirectory, SkillFile.skill)

    let skillPath = if File.Exists modelPath then modelPath else skillPath

    if not (File.Exists skillPath) then
        raise (SkillFormatError(skillPath, "SKILL.mdが存在しません"))

    let frontmatter = parseFrontmatter skillPath (File.ReadAllText skillPath)

    // idとnameはModelFormにも同じ名前のフィールドがあり、
    // レコードの型は後から定義された方が優先されるため、明示して取り違えを防ぐ。
    let knowledge: KnowledgeReference list =
        frontmatter.Knowledge
        |> List.map (fun name ->
            { KnowledgeReference.Id = None
              Name = name
              Type = collectionType })

    { Id = frontmatter.Name
      BaseModelId = None
      Name = frontmatter.Name
      Meta =
        { Description = frontmatter.Description
          // 紐付けが無いスキルでキーごと省くのではなくnullを書くのは、
          // base_model_idと同じくOpen WebUIが未設定として受け取れるため。
          Knowledge = if List.isEmpty knowledge then None else Some knowledge }
      Params =
        { System = buildSystemPrompt skillDirectory skillPath frontmatter.Body
          // 紐付けが無いのに方式を指定すると、
          // このリポジトリと関係のない理由で選ばれた設定を上書きしてしまうため、
          // 自動RAGが必要なModelだけへ設定する。
          FunctionCalling =
            if List.isEmpty knowledge then
                None
            else
                Some legacyFunctionCalling }
      IsActive = true }

/// JSON直列化の設定。
/// Open WebUIのPydanticモデルはbase_model_idのようなsnake_caseのキーを使うため、
/// キーの命名はsnake_caseへ揃える。
/// F#のoption型をnullと値の対応で書けるようにJsonFSharpOptionsを使い、
/// 日本語をエスケープせずそのまま書いて内容を確認できるようにする。
let private serializerOptions =
    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    JsonFSharpOptions.Default().AddToJsonSerializerOptions options
    options

/// ModelFormをOpen WebUIのAPIへ渡せるJSON文字列へ直列化する。
let toJson (form: ModelForm) : string =
    JsonSerializer.Serialize(form, serializerOptions) + "\n"

/// 管理対象のoption型のフィールドのうち、
/// 応答に無ければnullで補う必要があるものの位置。
/// 登録済みのModelには、
/// このリポジトリが後から管理対象へ足したフィールドがまだ無いことがあり、
/// 例えばfunction_callingを導入する前に登録したModelのparamsにはsystemしかありません。
/// レコードのフィールドが1つでも欠けるとデシリアライズは失敗しますが、
/// nullが入っていればNoneとして読めます。
///
/// option型のフィールドを足す時はここへも足してください。
/// 忘れると登録済みのModelを読み戻せなくなります。
/// 古い形の応答を読むテストが検出します。
let private optionalFieldPaths =
    [ [], "base_model_id"
      [ "meta" ], "knowledge"
      [ "params" ], "function_calling" ]

/// 応答に無いoption型のフィールドをnullで補う。
/// 途中の親が応答に無ければ補う先も無いため、そこで辿るのをやめる。
let private fillMissingOptionalFields (root: JsonNode) : JsonNode =
    for parentKeys, key in optionalFieldPaths do
        let parent =
            List.fold
                (fun (node: JsonNode option) (parentKey: string) ->
                    node |> Option.bind (fun node -> Option.ofObj node[parentKey]))
                (Some root)
                parentKeys

        match parent with
        | Some(:? JsonObject as object) when not (object.ContainsKey key) -> object[key] <- null
        | _ -> ()

    root

/// 読み込んだJSONのノードをModelFormへ読み戻す。
/// ModelFormに無いフィールドは無視されるため、
/// APIの応答を管理対象のフィールドだけへ正規化する用途にも使える。
///
/// originはこのJSONがどこから来たのかを示す、
/// ファイルのパスやURLのような短い文字列で、失敗した時の手掛かりになる。
/// 応答を読んだ後にそのノードを別の用途へ使い回せるように、
/// 文字列ではなくノードを受け取る形にしている。
let ofJsonNode (origin: string) (node: JsonNode) : ModelForm =
    try
        (fillMissingOptionalFields node).Deserialize<ModelForm>(serializerOptions)
    with :? JsonException as error ->
        raise (SkillFormatError(origin, $"Modelの定義として解釈できません: %s{error.Message}"))

/// toJsonの逆変換としてJSONをModelFormへ読み戻す。
let ofJson (origin: string) (json: string) : ModelForm =
    match JsonNode.Parse json with
    | null -> raise (SkillFormatError(origin, $"Modelの定義として解釈できません: %s{json}"))
    | root -> ofJsonNode origin root

/// スキルディレクトリからModelFormのJSONを書き出す。
/// 出力はNixのビルド成果物でリポジトリへはコミットしないため、nix fmtは掛けない。
let writeModel (skillDirectory: string) (outputPath: string) : Task<unit> =
    task {
        match Path.GetDirectoryName outputPath with
        | null
        | "" -> ()
        | directory -> Directory.CreateDirectory directory |> ignore

        do! File.WriteAllTextAsync(outputPath, toJson (buildModelForm skillDirectory))
    }
