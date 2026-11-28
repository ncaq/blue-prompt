/// スキルをOpen WebUIのワークスペースModel定義へ変換する。
/// Open WebUIにはClaude Codeのスキルのような、
/// 指示書を会話の途中で読み込む仕組みが無いため、
/// SKILL.mdの本文をシステムプロンプトへ焼き込んだ、
/// カスタムモデル(ModelForm)のJSONを生成する。
/// 読ませたい参照データは本文が節として持っている前提で、
/// ここでは参照ファイルを解決しない。
/// 生成したJSONはPOST /api/v1/models/createへそのまま渡して登録できる。
module BluePrompt.OpenWebui

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open XParsec
open XParsec.CharParsers
open YamlDotNet.Core
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions

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
/// open-webui syncが名前でコレクションを引き当てて埋める。
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
///
/// Knowledgeは省略可能なknowledge:行で、
/// そのスキルがOpen WebUIで参照するKnowledgeコレクションの名前を並べる。
/// Claude Codeではスキルを会話の途中で読み込めるが、
/// Open WebUIのModelは会話の入口を選ぶだけで他のModelを読み込めないため、
/// 参照させたいナレッジをModelへ紐付ける必要がある。
type Frontmatter =
    {
        Name: string
        Description: string
        Knowledge: string list
        Body: string
        /// 区切り行を含むフロントマターの生テキスト。
        /// 解釈した要素から組み直すと、
        /// このリポジトリが読まない項目を書き足した時に黙って落ちるため、
        /// 生成物へそのまま写したい呼び出し元のために持つ。
        Raw: string
    }

/// フロントマターの区切り行。
let private delimiter = "---"

/// フロントマターの区切りだけの行。
let private delimiterLine: Parse.TextParser<unit> =
    Parse.lineChoose (fun line -> if line = delimiter then Some() else None)

/// 区切り行に挟まれたフロントマターの行の並びと、その後ろの本文。
/// 閉じの区切り行が無ければ読めない。
/// フロントマターを落とすだけのOpenWebuiKnowledgeも同じ形を読むため公開している。
let frontmatterBlock: Parse.TextParser<struct (string list * string)> =
    let fieldLines: Parse.TextParser<string list> =
        many (Parse.lineChoose (fun line -> if line = delimiter then None else Some line))
        |>> List.ofSeq

    delimiterLine >>. fieldLines .>> delimiterLine .>>. Parse.restOfInput

/// フロントマターのうち、このリポジトリが読む項目。
/// 書かれていない項目はnullとして読まれる。
/// YamlDotNetが引数無しで作ってから値を書き込むため、CLIMutableにする。
/// privateにするとYamlDotNetが型を作れずに読み込みが失敗する。
[<CLIMutable>]
type FrontmatterFields =
    { Name: string
      Description: string
      Knowledge: string }

/// フロントマターをFrontmatterFieldsへ読むYAMLのデシリアライザ。
/// `user-invocable`のようにこのリポジトリが読まない項目はRawとして写すだけなので、
/// 型に無い項目があっても失敗させない。
let private yamlDeserializer =
    DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build()

/// SKILL.mdの本文からフロントマターの要素と、その後ろの本文を取り出す。
/// 必須の要素が欠けているなど形式が想定と異なる場合は、
/// 生成を黙って続けずSkillFormatErrorで止める。
/// 省略できる要素は、書かれていなければ空の値として扱う。
///
/// Modelを組み立てるOpenWebuiだけでなく、
/// Knowledgeを組み立てるOpenWebuiKnowledgeも同じ解釈を必要とするため公開している。
let parseFrontmatter (path: string) (content: string) : Frontmatter =
    let fail message = raise (SkillFormatError(path, message))

    let normalized = content.Replace("\r\n", "\n")

    match Parse.tryPrefix frontmatterBlock normalized with
    | None when Parse.startsWith delimiterLine normalized -> fail "フロントマターの閉じ---がありません"
    | None -> fail "フロントマターの開始---がありません"
    | Some struct (lines, body) ->
        let fields =
            try
                // 項目が1つも無いフロントマターは、空の文書としてnullに読まれる。
                match
                    box (yamlDeserializer.Deserialize<FrontmatterFields>(String.concat "\n" lines))
                with
                | null -> None
                | parsed -> Some(parsed :?> FrontmatterFields)
            with :? YamlException as error ->
                fail $"フロントマターをYAMLとして読めません: %s{error.Message}"

        let field (select: FrontmatterFields -> string) : string option =
            fields
            |> Option.bind (fun fields ->
                match select fields with
                | Str.NullOrEmpty -> None
                | Str.NonEmpty value -> Some value)

        let required key select =
            match field select with
            | Some value -> value
            | None -> fail $"フロントマターに%s{key}がありません"

        // knowledge:は紐付けの無いスキルでは書かないため、
        // 欠けていることを失敗とせず空の一覧として扱う。
        let knowledge =
            match field _.Knowledge with
            | None -> []
            | Some value ->
                value.Split ','
                |> Array.toList
                |> List.choose (fun name ->
                    match name.Trim() with
                    | Str.NullOrEmpty -> None
                    | Str.NonEmpty trimmed -> Some trimmed)

        { Name = required "name" _.Name
          Description = required "description" _.Description
          Knowledge = knowledge
          Body = body.Trim()
          Raw = String.concat "\n" (delimiter :: lines @ [ delimiter ]) }

/// URLのスキーム。`https:`のような形で参照ファイルのパスと区別する。
let private scheme: Parse.TextParser<char> =
    asciiLetter
    >>. skipManySatisfies (fun c -> isAsciiLetter c || isDigit c || c = '+' || c = '.' || c = '-')
    >>. pchar ':'

/// 本文中で明示的にリンクされている参照ファイルの相対パスを、
/// 登場順の重複なしで列挙する。
/// スキーム付きのURLとページ内アンカーは参照ファイルではないので除外し、
/// それ以外のリンクは黙って無視せず全て参照ファイルとして扱う。
/// 表記ゆれの`./`は落として`./`有りと無しが同じファイルへまとまるようにする。
let localLinkTargets (body: string) : string list =
    Markdown.linkUrls body
    |> Seq.filter (fun target ->
        not (target.StartsWith '#') && not (Parse.startsWith scheme target))
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

/// 本文が参照ファイルへのリンクを持っていないことを確かめる。
/// Modelのシステムプロンプトは1つの文字列で、
/// 会話の途中でファイルを開く手段がOpen WebUIには無いため、
/// リンクを書いても読み手には開けない参照が残るだけになる。
/// 読ませたい参照データは本文そのものへ節として書く。
let private checkNoLocalLinks (skillPath: string) (body: string) : unit =
    match localLinkTargets body with
    | [] -> ()
    | targets ->
        let joined = String.concat ", " targets

        raise (SkillFormatError(skillPath, $"Open WebUIでは開けないリンクが本文にあります: %s{joined}"))

/// スキルディレクトリからModelFormを組み立てる。
/// idとnameにはフロントマターのnameを使い、ここでは一意性を保証しない。
/// 同じ一覧の中のidの重複は、open-webui syncが同期の前に検出して止める。
let buildModelForm (skillDirectory: string) : ModelForm =
    let skillPath = Path.Combine(skillDirectory, SkillFile.skill)

    if not (File.Exists skillPath) then
        raise (SkillFormatError(skillPath, "SKILL.mdが存在しません"))

    let frontmatter = parseFrontmatter skillPath (File.ReadAllText skillPath)

    // 本文はそのままシステムプロンプトになるので、自足していることをここで確かめる。
    checkNoLocalLinks skillPath frontmatter.Body

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
        { System = frontmatter.Body + "\n"
          // 紐付けが無いのに方式を指定すると、
          // このリポジトリと関係のない理由で選ばれた設定を上書きしてしまうため、
          // 自動RAGが必要なModelだけへ設定する。
          FunctionCalling =
            if List.isEmpty knowledge then
                None
            else
                Some legacyFunctionCalling }
      IsActive = true }

/// ModelFormをOpen WebUIのAPIへ渡せるJSON文字列へ直列化する。
let toJson (form: ModelForm) : string = OpenWebuiJson.serialize form

/// toJsonの逆変換としてJSONをModelFormへ読み戻す。
/// ModelFormに無いフィールドは無視されるため、
/// APIの応答を管理対象のフィールドだけへ正規化する用途にも使える。
/// 応答にoption型のフィールドが無ければNoneとして読む。
///
/// originはこのJSONがどこから来たのかを示す、
/// ファイルのパスやURLのような短い文字列で、失敗した時の手掛かりになる。
let ofJson (origin: string) (json: string) : ModelForm =
    try
        OpenWebuiJson.deserialize<ModelForm> json
    with :? JsonException as error ->
        raise (SkillFormatError(origin, $"Modelの定義として解釈できません: %s{error.Message}"))

/// スキルディレクトリからModelFormのJSONを書き出す。
/// 出力はNixのビルド成果物でリポジトリへはコミットしないため、nix fmtは掛けない。
let writeModel (skillDirectory: string) (outputPath: string) : Task<unit> =
    task {
        match Path.GetDirectoryName outputPath with
        | Str.NullOrEmpty -> ()
        | Str.NonEmpty directory -> Directory.CreateDirectory directory |> ignore

        do! File.WriteAllTextAsync(outputPath, toJson (buildModelForm skillDirectory))
    }
