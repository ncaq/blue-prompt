/// スキルをOpen WebUIのワークスペースModel定義へ変換する。
/// Open WebUIにはClaude Codeのスキルのような、
/// 指示書と参照ファイルの組をオンデマンドで読み込む仕組みが無いため、
/// SKILL.mdと明示的にリンクされた参照ファイルをインライン化して、
/// システムプロンプトへ焼き込んだカスタムモデル(ModelForm)のJSONを生成する。
/// 生成したJSONはPOST /api/v1/models/createへそのまま渡して登録できる。
module BluePrompt.OpenWebui

open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Encodings.Web
open System.Text.RegularExpressions
open System.Threading.Tasks

/// SKILL.mdがOpen WebUIへ変換できる形式ではなかった時の対象パスと理由。
exception SkillFormatError of path: string * message: string

/// Open WebUIのModelFormのmetaフィールド。
/// backend/open_webui/models/models.pyのModelMetaに対応する。
/// UIの一覧でモデルの説明として表示される。
type Meta = { Description: string }

/// Open WebUIのModelFormのparamsフィールド。
/// ModelParamsはextra=allowの自由形式で、systemがシステムプロンプトになる。
type Params = { System: string }

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
/// このリポジトリのスキルが使うのは1行のname:とdescription:だけなので、
/// YAMLパーサーを持ち込まずkey: valueの行だけを解釈する。
type private Frontmatter =
    { Name: string
      Description: string
      Body: string }

/// フロントマターの区切り行。
let private delimiter = "---"

/// フロントマターのkey: value行。
let private fieldPattern = Regex @"^([A-Za-z_-]+):\s*(.*)$"

/// SKILL.mdの本文からフロントマターのnameとdescriptionを取り出す。
/// 形式が想定と異なる場合は生成を黙って続けずSkillFormatErrorで止める。
let private parseFrontmatter (path: string) (content: string) : Frontmatter =
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

            { Name = field "name"
              Description = field "description"
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
        if target.StartsWith("./", System.StringComparison.Ordinal) then
            target.Substring 2
        else
            target)
    |> Seq.distinct
    |> Seq.toList

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
/// 本文の「ファイルを読む」指示をインライン化後の読み替えへ接続する。
let private inlineNotice =
    "本文がリンクで参照しているファイルは、以下へインライン化済みです。\nファイルを読む指示は該当する節を読むことへ読み替えてください。"

/// スキルディレクトリのSKILL.mdと参照ファイルからシステムプロンプト全文を組み立てる。
/// リンクされたファイルが実在しない場合は参照が壊れているのでSkillFormatErrorで止める。
let private buildSystemPrompt (skillDirectory: string) (skillPath: string) (body: string) : string =
    let sections =
        localLinkTargets body
        |> List.map (fun fileName ->
            // ディレクトリを遡る参照はスキルディレクトリの外に出るため受け付けない。
            if fileName.Split '/' |> Array.contains ".." then
                raise (SkillFormatError(skillPath, $"%s{fileName}はスキルディレクトリの外を参照しています"))

            let filePath = Path.Combine(skillDirectory, fileName)

            if not (File.Exists filePath) then
                raise (SkillFormatError(skillPath, $"リンクされた%s{fileName}が存在しません"))

            inlineSection fileName (File.ReadAllText filePath))

    match sections with
    | [] -> body + "\n"
    | _ ->
        [ body; delimiter; inlineNotice ] @ sections
        |> String.concat "\n\n"
        |> fun prompt -> prompt + "\n"

/// スキルディレクトリからModelFormを組み立てる。
/// idとnameにはフロントマターのnameを使う。
/// スキル名のプラグイン間の衝突はflake.nixの評価時に検出される前提。
let buildModelForm (skillDirectory: string) : ModelForm =
    let skillPath = Path.Combine(skillDirectory, "SKILL.md")

    if not (File.Exists skillPath) then
        raise (SkillFormatError(skillPath, "SKILL.mdが存在しません"))

    let frontmatter = parseFrontmatter skillPath (File.ReadAllText skillPath)

    { Id = frontmatter.Name
      BaseModelId = None
      Name = frontmatter.Name
      Meta = { Description = frontmatter.Description }
      Params = { System = buildSystemPrompt skillDirectory skillPath frontmatter.Body }
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

/// toJsonの逆変換としてJSONをModelFormへ読み戻す。
/// ModelFormに無いフィールドは無視されるため、
/// APIの応答を管理対象のフィールドだけへ正規化する用途にも使える。
let ofJson (json: string) : ModelForm =
    JsonSerializer.Deserialize<ModelForm>(json, serializerOptions)

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
