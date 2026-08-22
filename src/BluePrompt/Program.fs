module BluePrompt.Program

open System.Threading.Tasks
open Argu

// サブコマンドは外部システムとの接点で階層へ分ける。
// wikiruから取り込むものはwikiru、
// 外部と話さずリポジトリ内で完結するものはroleplay、
// Open WebUI向けの生成と同期はopen-webuiにぶら下がる。
// 並び順はこの取り込みから送り出しへの流れの順で、
// 各グループの中は辞書順、生成物を作らない確認用だけを末尾へ置く。
// 判別共用体のケースの並びがそのままヘルプの並びになるため、順序の定義はここだけにある。
//
// 同じケース名が複数の階層に現れるため、判別共用体は1つずつモジュールへ入れる。
// F#は後から宣言したケースが前のものを隠すので、
// モジュールで名前空間を分けないと`Knowledge`のような名前が意図しない型へ解決される。
// RequireQualifiedAccessでも隠れなくなるが、
// FCSのSimplifyNamesがこの属性を見ておらず、
// 省略するとコンパイルが通らない修飾まで冗長と報告するためリントが通らない。

/// wikiruのページ名と単一の出力先だけを取るコマンドの引数。
module PageOutput =
    type Args =
        | [<ExactlyOnce>] Page of name: string
        | [<ExactlyOnce>] Output of path: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Page _ -> "取得するwikiruのページ名。"
                | Output _ -> "書き出すファイルのパス。"

/// キャラ呼称表は参照用と機械読み出し用の2つを書き出すため、出力先も2つ取る。
module AppellationOutput =
    type Args =
        | [<ExactlyOnce>] Page of name: string
        | [<ExactlyOnce>] Markdown_Output of path: string
        | [<ExactlyOnce>] Json_Output of path: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Page _ -> "取得するwikiruのページ名。"
                | Markdown_Output _ -> "LLM参照用のreference.mdの出力パス。"
                | Json_Output _ -> "機械読み出し用のappellation.jsonの出力パス。"

/// スキルのディレクトリを読んで単一の出力先へ書き出すコマンドの引数。
module SkillOutput =
    type Args =
        | [<ExactlyOnce>] Skill of directory: string
        | [<ExactlyOnce>] Output of path: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Skill _ -> "変換元のスキルのディレクトリ。"
                | Output _ -> "書き出す先のパス。"

module RolePlaySkill =
    type Args =
        | [<ExactlyOnce>] Character of name: string
        | [<ExactlyOnce>] Template of directory: string
        | [<ExactlyOnce>] Appellation of path: string
        | [<ExactlyOnce>] Output of directory: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Character _ -> "演じる生徒の呼び名。"
                | Template _ -> "SKILL.template.mdとMODEL.template.mdを置いたディレクトリ。"
                | Appellation _ -> "生成済みのappellation.jsonのパス。"
                | Output _ -> "SKILL.mdとMODEL.mdを書き出すスキルのディレクトリ。"

module Sync =
    type Args =
        | [<ExactlyOnce>] Model of directory: string
        | [<ExactlyOnce>] Base_Url of url: string
        | [<Unique>] Base_Model_Id of id: string
        | [<Unique>] Api_Key_File of path: string
        | [<Unique>] Knowledge of directory: string
        | [<Unique>] Rag_Template_File of path: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Model _ -> "open-webui modelが生成したModelFormのJSON群のディレクトリ。"
                | Base_Url _ -> "同期先のOpen WebUIのベースURL。"
                | Base_Model_Id _ -> "生成したModelの土台にするモデルのid。"
                | Api_Key_File _ -> "APIキーを書いたファイルのパス。省略すると認証を無効にしたインスタンスとして扱う。"
                | Knowledge _ -> "open-webui knowledgeの生成物のディレクトリ。与えるとKnowledgeも同期して紐付ける。"
                | Rag_Template_File _ -> "同期するRAGプロンプトテンプレートのファイル。"

// CliPrefixを型へ適用するとArguが用意するヘルプのフラグからも接頭辞が落ちるため、
// サブコマンドの階層でも`--help`が通るように明示する。
module WikiruCommand =
    [<CliPrefix(CliPrefix.None); HelpFlags("--help", "-h", "help")>]
    type Args =
        | Appellation of ParseResults<AppellationOutput.Args>
        | Knowledge of ParseResults<PageOutput.Args>
        | Roleplay_Reference of ParseResults<PageOutput.Args>
        | School of ParseResults<PageOutput.Args>
        | Student_Skill of ParseResults<PageOutput.Args>
        | Html of ParseResults<PageOutput.Args>
        | Student_Html of ParseResults<PageOutput.Args>

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Appellation _ -> "キャラ呼称表を構造化し、参照用のreference.mdと機械読み出し用のJSONを書き出す。"
                | Knowledge _ -> "記事をMarkdown化してナレッジファイルとして書き出す。"
                | Roleplay_Reference _ -> "生徒個別ページからプロフィールとボイスを抜き出し、衣装別の参照ファイルとして書き出す。"
                | School _ -> "学校別キャラクター一覧を構造化し、学校ごとの一覧のreference.mdを書き出す。"
                | Student_Skill _ -> "生徒個別ページから事実セクションを抜き出し、スキル定義ごとSKILL.mdとして書き出す。"
                | Html _ -> "記事から抽出した本文をMarkdown化せずHTMLのまま書き出す。抽出設定の確認用。"
                | Student_Html _ -> "生徒個別ページの抽出設定で本文をHTMLのまま書き出す。抽出設定の確認用。"

module RolePlayCommand =
    [<CliPrefix(CliPrefix.None); HelpFlags("--help", "-h", "help")>]
    type Args =
        | Skill of ParseResults<RolePlaySkill.Args>

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Skill _ -> "テンプレートへcharacter.mdと衣装別の参照ファイルの一覧と呼称表を流し込んで書き出す。"

module OpenWebuiCommand =
    [<CliPrefix(CliPrefix.None); HelpFlags("--help", "-h", "help")>]
    type Args =
        | Knowledge of ParseResults<SkillOutput.Args>
        | Model of ParseResults<SkillOutput.Args>
        | Sync of ParseResults<Sync.Args>

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Knowledge _ -> "SKILL.mdとリンクされた参照ファイルを見出しの単位へ分割してKnowledgeの定義を書き出す。"
                | Model _ -> "MODEL.mdとリンクされた参照ファイルをインライン化してModelFormのJSONを書き出す。"
                | Sync _ -> "生成したModelとKnowledgeをインスタンスへ同期する。差分が無ければ書き込まない。"

module RootCommand =
    [<CliPrefix(CliPrefix.None); HelpFlags("--help", "-h", "help")>]
    type Args =
        | Wikiru of ParseResults<WikiruCommand.Args>
        | Roleplay of ParseResults<RolePlayCommand.Args>
        | Open_Webui of ParseResults<OpenWebuiCommand.Args>

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Wikiru _ -> "wikiruの記事を取得して生成物を書き出す。"
                | Roleplay _ -> "リポジトリ内の生成物だけでrole-playスキルの本文を組み立てる。"
                | Open_Webui _ -> "Open WebUI向けの定義を生成してインスタンスへ同期する。"

/// サブコマンドの階層を全て展開した使い方。
/// この規模なら階層を辿らせるより、どんなコマンドがあるのか一度に見せるほうが分かりやすい。
let rec expandedUsage (parser: ArgumentParser) : string seq =
    seq {
        yield parser.PrintUsage()
        yield! parser.GetSubCommandParsers() |> Seq.collect expandedUsage
    }

/// 書き出しを待って終了コードにする。
let private run (work: Task<unit>) : int =
    work.GetAwaiter().GetResult()
    0

let private runWikiru (args: ParseResults<WikiruCommand.Args>) : int =
    /// wikiruのページ名と出力先だけを取るコマンドの共通の取り出し。
    let pageOutput (sub: ParseResults<PageOutput.Args>) =
        sub.GetResult PageOutput.Page, sub.GetResult PageOutput.Output

    match args.GetSubCommand() with
    | WikiruCommand.Appellation sub ->
        run (
            Wikiru.writeAppellation
                (sub.GetResult AppellationOutput.Page)
                (sub.GetResult AppellationOutput.Markdown_Output)
                (sub.GetResult AppellationOutput.Json_Output)
        )
    | WikiruCommand.Knowledge sub -> run (pageOutput sub ||> Wikiru.writeKnowledge)
    | WikiruCommand.Roleplay_Reference sub -> run (pageOutput sub ||> Wikiru.writeRolePlayReference)
    | WikiruCommand.School sub -> run (pageOutput sub ||> Wikiru.writeSchool)
    | WikiruCommand.Student_Skill sub -> run (pageOutput sub ||> Wikiru.writeStudentSkill)
    | WikiruCommand.Html sub -> run (pageOutput sub ||> Wikiru.writeContentHtml Wikiru.contentQuery)
    | WikiruCommand.Student_Html sub ->
        run (pageOutput sub ||> Wikiru.writeContentHtml Wikiru.studentContentQuery)

let private runRolePlay (args: ParseResults<RolePlayCommand.Args>) : int =
    match args.GetSubCommand() with
    | RolePlayCommand.Skill sub ->
        run (
            RolePlay.writeSkill
                (sub.GetResult RolePlaySkill.Character)
                (sub.GetResult RolePlaySkill.Template)
                (sub.GetResult RolePlaySkill.Appellation)
                (sub.GetResult RolePlaySkill.Output)
        )

/// 同期先への接続情報を引数から組み立てる。
let syncOptions (args: ParseResults<Sync.Args>) : OpenWebuiSync.Options =
    { ModelsDirectory = args.GetResult Sync.Model
      Url = args.GetResult Sync.Base_Url
      BaseModelId = args.TryGetResult Sync.Base_Model_Id
      ApiKeyFile = args.TryGetResult Sync.Api_Key_File
      KnowledgeDirectory = args.TryGetResult Sync.Knowledge
      RagTemplateFile = args.TryGetResult Sync.Rag_Template_File }

let private runOpenWebui (args: ParseResults<OpenWebuiCommand.Args>) : int =
    let skillOutput (sub: ParseResults<SkillOutput.Args>) =
        sub.GetResult SkillOutput.Skill, sub.GetResult SkillOutput.Output

    match args.GetSubCommand() with
    | OpenWebuiCommand.Knowledge sub -> run (skillOutput sub ||> OpenWebuiKnowledge.writeKnowledge)
    | OpenWebuiCommand.Model sub -> run (skillOutput sub ||> OpenWebui.writeModel)
    | OpenWebuiCommand.Sync sub -> run (OpenWebuiSync.sync (syncOptions sub))

[<EntryPoint>]
let main argv =
    // 既定のExceptionExiterのまま例外で受けて、終了コードを自分で決める。
    // ProcessExiterはその場でプロセスを終わらせるため、テストからmainを呼べなくなる。
    let parser = ArgumentParser.Create<RootCommand.Args>(programName = "blue-prompt")

    // 使い方の確認はエラーではないので標準出力へ出す。
    // ヘルプのフラグはArguが持つものをそのまま使い、ここでは並べ直さない。
    let printExpandedUsage () =
        printfn $"""%s{expandedUsage parser |> String.concat "\n"}"""
        0

    try
        match argv with
        | [||] -> printExpandedUsage ()
        | [| flag |] when List.contains flag parser.HelpFlags -> printExpandedUsage ()
        | _ ->
            match (parser.ParseCommandLine argv).GetSubCommand() with
            | RootCommand.Wikiru args -> runWikiru args
            | RootCommand.Roleplay args -> runRolePlay args
            | RootCommand.Open_Webui args -> runOpenWebui args
    with
    | :? ArguParseException as e ->
        // ヘルプの要求は求めた通りの結果なので、標準出力へ出して正常終了にする。
        if e.ErrorCode = ErrorCode.HelpText then
            printfn $"%s{e.Message}"
            0
        else
            eprintfn $"%s{e.Message}"
            1
    // 同期の失敗はスタックトレースではなく理由だけを表示する。
    | OpenWebuiSync.SyncError message ->
        eprintfn $"%s{message}"
        1
