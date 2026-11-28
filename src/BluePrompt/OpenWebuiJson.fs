/// Open WebUIとやり取りするJSONの直列化。
/// ModelとKnowledgeの生成物も、同期でAPIへ送るボディも読む応答も、
/// 全てF#のレコードとの対応で書き、JSONの木を手で辿らない。
module BluePrompt.OpenWebuiJson

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization

/// 書き出しと読み込みの共通の設定。
/// Open WebUIのPydanticモデルはbase_model_idのようなsnake_caseのキーを使うため、
/// キーの命名はsnake_caseへ揃える。
/// F#のoption型をnullと値の対応で書けるようにJsonFSharpOptionsを使い、
/// 日本語をエスケープせずそのまま書いて内容を確認できるようにする。
let private optionsWith (fsharpOptions: JsonFSharpOptions) : JsonSerializerOptions =
    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    fsharpOptions.AddToJsonSerializerOptions options
    options

/// 書き出しの設定。
/// Noneのフィールドもキーごと省かずにnullとして書く。
/// Open WebUIはnullを未設定として受け取れるうえ、
/// 生成物を読んだ時にどのフィールドが未設定なのかが見えるため。
/// APIへ送るボディを`System.Net.Http.Json`で直列化する時にも渡すため公開している。
let writeOptions =
    optionsWith (JsonFSharpOptions.Default().WithSkippableOptionFields SkippableOptionFields.Never)

/// 読み込みの設定。
/// option型のフィールドは、JSONに無くてもnullでもNoneとして読む。
/// 登録済みのModelには、
/// このリポジトリが後から管理対象へ足したフィールドがまだ無いことがあり、
/// 例えばfunction_callingを導入する前に登録したModelのparamsにはsystemしかない。
/// 書き出しと同じ設定では、option型のフィールドが1つでも欠けると読み込みが失敗する。
/// 逆に省けることだけを許すと、
/// 書き出しがnullとして書いたフィールドをSomeの中身として読もうとして失敗する。
/// APIの応答を`System.Net.Http.Json`で読む時にも渡すため公開している。
let readOptions =
    optionsWith (
        JsonFSharpOptions
            .Default()
            .WithSkippableOptionFields(SkippableOptionFields.Always, deserializeNullAsNone = true)
    )

/// 値をJSON文字列へ直列化する。ファイルとしても書き出すため末尾に改行を付ける。
let serialize<'T> (value: 'T) : string =
    JsonSerializer.Serialize(value, writeOptions) + "\n"

/// JSON文字列を値へ読む。
/// option型ではないフィールドが無いJSONや、`null`だけのJSONはJsonExceptionで失敗する。
let deserialize<'T> (json: string) : 'T =
    let value = JsonSerializer.Deserialize<'T>(json, readOptions)

    match box value with
    | null -> raise (JsonException "JSONがnullです")
    | _ -> value
