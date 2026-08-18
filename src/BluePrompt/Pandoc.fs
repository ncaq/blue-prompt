/// pandoc CLIを起動してHTMLをMarkdownへ変換する。
module BluePrompt.Pandoc

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks

/// pandocが異常終了した時のexit codeとstderr。
exception PandocError of exitCode: int * stderr: string

/// HTMLをLLM・人間可読なMarkdownへ変換するためのpandocの引数。
/// gfmで出力し、raw_htmlを無効化して変換できないタグを除去し、行折り返しをしない。
/// --sandboxでiframeのsrc取得などのIOを禁止する(信頼できないHTMLによるSSRF対策)。
let private markdownArguments =
    [ "-f"; "html"; "-t"; "gfm-raw_html"; "--wrap=none"; "--sandbox" ]

/// 環境変数PANDOC_PATHがあればそれを、無ければPATH上のpandocを使う。
let resolvePath () : string =
    Environment.GetEnvironmentVariable "PANDOC_PATH"
    |> Option.ofObj
    |> Option.defaultValue "pandoc"

/// HTML文字列をLLMにも人間にも読みやすいGFM Markdownへ変換する。
/// raw_htmlを無効化して変換できないタグを除去し、行折り返しをしない。
let toMarkdown (html: string) : Task<string> =
    task {
        let startInfo =
            ProcessStartInfo(
                FileName = resolvePath (),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            )

        for argument in markdownArguments do
            startInfo.ArgumentList.Add argument

        use pandoc = Process.Start startInfo
        do! pandoc.StandardInput.WriteAsync html
        pandoc.StandardInput.Close()
        let! markdown = pandoc.StandardOutput.ReadToEndAsync()
        let! stderr = pandoc.StandardError.ReadToEndAsync()
        do! pandoc.WaitForExitAsync()

        if pandoc.ExitCode <> 0 then
            raise (PandocError(exitCode = pandoc.ExitCode, stderr = stderr))

        return markdown
    }
