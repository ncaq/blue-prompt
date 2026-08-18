/// pandoc CLIを起動してHTMLをMarkdownへ変換する。
module BluePrompt.Pandoc

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks

/// pandocが異常終了した時のexit codeとstderr。
exception PandocError of exitCode: int * stderr: string

/// pandocの完了をこれ以上待たない打ち切り時間。
let private timeout = TimeSpan.FromMinutes 1.

/// HTMLをLLM・人間可読なMarkdownへ変換するためのpandocの引数。
/// gfmで出力し、raw_htmlを無効化して変換できないタグを除去し、行折り返しをしない。
/// --sandboxでiframeのsrc取得などのIOを禁止する(信頼できないHTMLによるSSRF対策)。
let private markdownArguments =
    [ "-f"; "html"; "-t"; "gfm-raw_html"; "--wrap=none"; "--sandbox" ]

/// 環境変数PANDOC_PATHがあればそれを、無ければPATH上のpandocを使う。
let resolvePath () : string =
    match Environment.GetEnvironmentVariable "PANDOC_PATH" with
    | path when not (String.IsNullOrWhiteSpace path) -> path
    | _ -> "pandoc"

/// 指定したパスのpandocでHTML文字列をGFM Markdownへ変換する。
/// pandocが非0終了した場合はPandocErrorを、
/// 打ち切り時間を超えた場合はTimeoutExceptionを送出する。
let toMarkdownWith (pandocPath: string) (html: string) : Task<string> =
    task {
        let startInfo =
            ProcessStartInfo(
                FileName = pandocPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            )

        for argument in markdownArguments do
            startInfo.ArgumentList.Add argument

        use cancellation = new CancellationTokenSource(timeout)

        use pandoc =
            match Process.Start startInfo with
            | null ->
                // PANDOC_PATHの設定ミスやdevShell外での実行を切り分けられるようにパスを含める。
                failwith $"pandocを起動できませんでした: %s{startInfo.FileName}"
            | started -> started
        // 標準入力の書き込み中にパイプバッファが満杯になるとpandocと相互待ちになるため、
        // 標準出力と標準エラーの読み取りを書き込みより先に開始しておく。
        let stdoutTask = pandoc.StandardOutput.ReadToEndAsync()
        let stderrTask = pandoc.StandardError.ReadToEndAsync()

        try
            do! pandoc.StandardInput.WriteAsync(html.AsMemory(), cancellation.Token)
            pandoc.StandardInput.Close()
            do! pandoc.WaitForExitAsync cancellation.Token
        with :? OperationCanceledException ->
            // 打ち切り時はプロセスを回収しないとパイプごとリークする。
            pandoc.Kill(entireProcessTree = true)
            raise (TimeoutException $"pandocが%.0f{timeout.TotalSeconds}秒以内に終了しませんでした")

        let! markdown = stdoutTask
        let! stderr = stderrTask

        if pandoc.ExitCode <> 0 then
            raise (PandocError(exitCode = pandoc.ExitCode, stderr = stderr))

        return markdown
    }

/// resolvePathで解決したpandocでHTML文字列をGFM Markdownへ変換する。
/// エラー条件はtoMarkdownWithと同じ。
let toMarkdown (html: string) : Task<string> = toMarkdownWith (resolvePath ()) html
