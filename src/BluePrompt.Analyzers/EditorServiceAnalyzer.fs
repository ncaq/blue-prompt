/// FSACがエディタで表示する診断をCLIのリントでも検出するアナライザー。
/// FSACはLSPサーバでバッチ実行の手段が無いため、
/// FSACが内部で呼んでいるFSharp.Compiler.ServiceのEditorServices APIを、
/// FSharp.Analyzers.SDKのアナライザーとして呼び出して同じ検出を再現する。
/// 診断コードはエディタの表示と突き合わせられるようにFSACのものへ揃える。
/// SeverityはFSACのHintと違ってリントの失敗として扱えるようにWarningへ上げる。
module BluePrompt.Analyzers.EditorServiceAnalyzer

open System
open FSharp.Analyzers.SDK
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text

[<Literal>]
let private helpUri = "https://github.com/ionide/FsAutoComplete"

/// EditorServices APIが要求する行取得関数。
/// APIへ渡す行番号は1始まりでSourceTextの行インデックスは0始まり。
let private getSourceLineStr (ctx: CliContext) (lineNumber: int) : string =
    ctx.SourceText.GetLineString(lineNumber - 1)

/// 検出結果の位置を診断のMessageへ写す。
let private toMessage
    (analyzerType: string)
    (code: string)
    (message: string)
    (range: range)
    : Message =
    { Type = analyzerType
      Message = message
      Code = code
      Severity = Severity.Warning
      Range = range
      Fixes = [] }

[<CliAnalyzer("UnusedOpensAnalyzer",
              "Detects unused open statements like FSAC does in editors.",
              helpUri)>]
let unusedOpensAnalyzer (ctx: CliContext) =
    async {
        let! unusedOpens = UnusedOpens.getUnusedOpens (ctx.CheckFileResults, getSourceLineStr ctx)

        return
            unusedOpens
            |> List.map (toMessage "UnusedOpens analyzer" "FSAC0001" "Unused open statement")
    }

[<CliAnalyzer("SimplifyNamesAnalyzer",
              "Detects redundant qualifiers like FSAC does in editors.",
              helpUri)>]
let simplifyNamesAnalyzer (ctx: CliContext) =
    async {
        let! simplifiableNames =
            SimplifyNames.getSimplifiableNames (ctx.CheckFileResults, getSourceLineStr ctx)

        return
            simplifiableNames
            |> Seq.map (fun simplifiableRange ->
                toMessage
                    "SimplifyNames analyzer"
                    "FSAC0002"
                    $"This qualifier is redundant: %s{simplifiableRange.RelativeName}"
                    simplifiableRange.Range)
            |> Seq.toList
    }

[<CliAnalyzer("UnusedDeclarationsAnalyzer",
              "Detects unused declarations like FSAC does in editors.",
              helpUri)>]
let unusedDeclarationsAnalyzer (ctx: CliContext) =
    async {
        let isScriptFile = ctx.FileName.EndsWith(".fsx", StringComparison.Ordinal)

        let! unusedDeclarations =
            UnusedDeclarations.getUnusedDeclarations (ctx.CheckFileResults, isScriptFile)

        return
            unusedDeclarations
            |> Seq.map (toMessage "UnusedDeclarations analyzer" "FSAC0003" "This value is unused")
            |> Seq.toList
    }
