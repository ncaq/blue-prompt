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

[<Literal>]
let private helpUri = "https://github.com/ionide/FsAutoComplete"

[<CliAnalyzer("UnusedOpensAnalyzer",
              "Detects unused open statements like FSAC does in editors.",
              helpUri)>]
let unusedOpensAnalyzer (ctx: CliContext) =
    async {
        let getSourceLineStr (lineNumber: int) =
            ctx.SourceText.GetLineString(lineNumber - 1)

        let! unusedOpens = UnusedOpens.getUnusedOpens (ctx.CheckFileResults, getSourceLineStr)

        return
            unusedOpens
            |> List.map (fun range ->
                { Type = "UnusedOpens analyzer"
                  Message = "Unused open statement"
                  Code = "FSAC0001"
                  Severity = Severity.Warning
                  Range = range
                  Fixes = [] })
    }

[<CliAnalyzer("SimplifyNamesAnalyzer",
              "Detects redundant qualifiers like FSAC does in editors.",
              helpUri)>]
let simplifyNamesAnalyzer (ctx: CliContext) =
    async {
        let getSourceLineStr (lineNumber: int) =
            ctx.SourceText.GetLineString(lineNumber - 1)

        let! simplifiableNames =
            SimplifyNames.getSimplifiableNames (ctx.CheckFileResults, getSourceLineStr)

        return
            simplifiableNames
            |> Seq.map (fun simplifiableRange ->
                { Type = "SimplifyNames analyzer"
                  Message = $"This qualifier is redundant: %s{simplifiableRange.RelativeName}"
                  Code = "FSAC0002"
                  Severity = Severity.Warning
                  Range = simplifiableRange.Range
                  Fixes = [] })
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
            |> Seq.map (fun range ->
                { Type = "UnusedDeclarations analyzer"
                  Message = "This value is unused"
                  Code = "FSAC0003"
                  Severity = Severity.Warning
                  Range = range
                  Fixes = [] })
            |> Seq.toList
    }
