/// 生成対象の型と、対象ごとの書き出しと整形。
/// 個別コマンドと一括更新のマニフェストが同じ型で対象を表し、
/// 種別と引数の対応と種別ごとの振り分けをここ1箇所に置く。
module BluePrompt.Target

open System.IO
open System.Threading.Tasks

/// wikiruから取り込む対象。wikiruの個別コマンドの種別と1対1に対応する。
/// 確認用のhtmlとstudent-htmlは生成物を作らないため含めない。
type WikiruTarget =
    | Appellation of page: string * markdownOutput: string * jsonOutput: string
    | Knowledge of page: string * output: string
    | School of page: string * output: string
    | StudentSkill of page: string * output: string
    | RolePlayReference of page: string * output: string

/// テンプレートから本文を生成するrole-playスキル。
type RolePlaySkill =
    { Caller: string
      Template: string
      Appellation: string
      Output: string }

/// 失敗の報告に使う対象の表示名。取得元のページ名で見分ける。
let wikiruName (target: WikiruTarget) : string =
    match target with
    | Appellation(page = page)
    | Knowledge(page = page)
    | School(page = page)
    | StudentSkill(page = page)
    | RolePlayReference(page = page) -> page

/// 失敗の報告に使う対象の表示名。演じる生徒の呼び名で見分ける。
let rolePlayName (skill: RolePlaySkill) : string = skill.Caller

/// 対象のパスをルートからの相対として解決する。
/// マニフェストはリポジトリのルートからの相対パスで対象を書くため、
/// 実行時に指定されたルートと結合してから書き出す。
let resolveWikiru (root: string) (target: WikiruTarget) : WikiruTarget =
    let resolve (path: string) = Path.Combine(root, path)

    match target with
    | Appellation(page, markdownOutput, jsonOutput) ->
        Appellation(page, resolve markdownOutput, resolve jsonOutput)
    | Knowledge(page, output) -> Knowledge(page, resolve output)
    | School(page, output) -> School(page, resolve output)
    | StudentSkill(page, output) -> StudentSkill(page, resolve output)
    | RolePlayReference(page, output) -> RolePlayReference(page, resolve output)

/// スキルのパスをルートからの相対として解決する。挙動はresolveWikiruと同じ。
let resolveRolePlay (root: string) (skill: RolePlaySkill) : RolePlaySkill =
    { skill with
        Template = Path.Combine(root, skill.Template)
        Appellation = Path.Combine(root, skill.Appellation)
        Output = Path.Combine(root, skill.Output) }

/// 対象をwikiruから取得して書き出し、書いたパスを返す。整形は掛けない。
let writeWikiru (target: WikiruTarget) : Task<string list> =
    match target with
    | Appellation(page, markdownOutput, jsonOutput) ->
        Wikiru.writeAppellation page markdownOutput jsonOutput
    | Knowledge(page, output) -> Wikiru.writeKnowledge page output
    | School(page, output) -> Wikiru.writeSchool page output
    | StudentSkill(page, output) -> Wikiru.writeStudentSkill page output
    | RolePlayReference(page, output) -> Wikiru.writeRolePlayReference page output

/// スキルの本文を併置された生成物から書き出し、書いたパスを返す。整形は掛けない。
let writeRolePlay (skill: RolePlaySkill) : Task<string list> =
    RolePlay.writeSkill skill.Caller skill.Template skill.Appellation skill.Output

/// 書き出した直後にnix fmtを掛けて、生成コマンドだけで内容が確定するようにする。
/// nixの起動とtreefmtの評価が所要時間の支配項なので、
/// 複数のファイルを書き出す対象も1回の起動でまとめて整形する。
let private writeThenFormat (write: Task<string list>) : Task<unit> =
    task {
        let! paths = write
        do! Fmt.formatFiles paths
    }

/// 対象を書き出してnix fmtまで掛ける。wikiruの個別コマンドの入口。
let createWikiru (target: WikiruTarget) : Task<unit> = writeThenFormat (writeWikiru target)

/// スキルの本文を書き出してnix fmtまで掛ける。roleplay skillコマンドの入口。
let createRolePlay (skill: RolePlaySkill) : Task<unit> = writeThenFormat (writeRolePlay skill)
