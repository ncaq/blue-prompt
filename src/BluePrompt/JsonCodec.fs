/// wikiruから構造化したレコードを機械読み出し用のJSONとして書き出す時の共通の直列化。
/// 呼称表と学校別一覧のように同じスキル群へ並ぶJSONの書式を揃えるため、設定を1箇所に置く。
module BluePrompt.JsonCodec

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization

/// JSON直列化の設定。
/// F#のoption型をnullと値の対応で書けるようにJsonFSharpOptionsを使い、
/// 日本語をエスケープせずそのまま書いてdiffを読めるようにする。
/// FSharp.SystemTextJson v1.1以降の公式推奨であるフルーエントビルダーで組み込む。
let private serializerOptions =
    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    JsonFSharpOptions.Default().AddToJsonSerializerOptions options
    options

/// 値をJSON文字列へ直列化する。ファイルとして書き出すため末尾に改行を付ける。
let serialize<'T> (value: 'T) : string =
    JsonSerializer.Serialize(value, serializerOptions) + "\n"

/// JSON文字列を値へ読み戻す。serializeの逆変換。
let deserialize<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, serializerOptions)
