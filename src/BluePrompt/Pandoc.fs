/// pandoc CLIを起動してHTMLをMarkdownへ変換する。
module BluePrompt.Pandoc

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// pandocが異常終了した時のexit codeとstderr。
exception PandocError of exitCode: int * stderr: string

/// pandocの完了をこれ以上待たない打ち切り時間。
let private timeout = TimeSpan.FromMinutes 1.

/// HTMLをLLM・人間可読なMarkdownへ変換するためのpandocの既定の引数。
/// gfmで出力し、raw_htmlを無効化して変換できないタグを除去し、行折り返しをしない。
/// --sandboxでiframeのsrc取得などのIOを禁止する(信頼できないHTMLによるSSRF対策)。
/// 呼び出し側はこれを差し替えたり書き足したりして変換を調整できる。
let defaultMarkdownArguments =
    [ "-f"; "html"; "-t"; "gfm-raw_html"; "--wrap=none"; "--sandbox" ]

/// 環境変数PANDOC_PATHがあればそれを、無ければPATH上のpandocを使う。
let resolvePath () : string =
    match Environment.GetEnvironmentVariable "PANDOC_PATH" with
    | path when not (String.IsNullOrWhiteSpace path) -> path
    | _ -> "pandoc"

/// 指定したパスのpandocを指定した引数で起動してHTML文字列をMarkdownへ変換する。
/// pandocが非0終了した場合はPandocErrorを、
/// 打ち切り時間を超えた場合はTimeoutExceptionを送出する。
/// 標準入力への書き込みが失敗した場合は終了状態で判断する。
/// 非0終了ならPandocErrorを優先し、
/// 0終了なら出力が完全である保証が無いため書き込み失敗のIOExceptionを送出する。
let toMarkdownWith (pandocPath: string) (arguments: string list) (html: string) : Task<string> =
    task {
        let startInfo =
            ProcessStartInfo(
                FileName = pandocPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Encoding.UTF8はBOM付きで、ProcessはBOMをプロセス起動直後に書き込む。
                // 起動直後に終了したプロセスへのBOM書き込みはbroken pipeとなり、
                // Start自体がIOExceptionで失敗する競合になるため、BOMなしを明示する。
                // pandocへの入力に余計なBOMが混ざることも防ぐ。
                StandardInputEncoding = UTF8Encoding false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            )

        for argument in arguments do
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

        // プロセスが入力を読み切る前に終了するとbroken pipeで書き込みが失敗する。
        // その場で送出せず記録に留めて、終了状態を見てから報告方法を決める。
        let mutable writeError: IOException option = None

        try
            try
                try
                    do! pandoc.StandardInput.WriteAsync(html.AsMemory(), cancellation.Token)
                with :? IOException as error ->
                    writeError <- Some error
            finally
                // Closeを飛ばすとEOFが送られず、プロセスが生きている場合に終了待ちが打ち切りまでブロックする。
                // Close自体の失敗(フラッシュのbroken pipe)も入力が完全に渡っていない兆候として記録する。
                try
                    pandoc.StandardInput.Close()
                with :? IOException as error ->
                    if writeError.IsNone then
                        writeError <- Some error

            do! pandoc.WaitForExitAsync cancellation.Token
        with :? OperationCanceledException ->
            // 打ち切り時はプロセスを回収しないとパイプごとリークする。
            pandoc.Kill(entireProcessTree = true)
            raise (TimeoutException $"pandocが%.0f{timeout.TotalSeconds}秒以内に終了しませんでした")

        let! markdown = stdoutTask
        let! stderr = stderrTask

        if pandoc.ExitCode <> 0 then
            raise (PandocError(exitCode = pandoc.ExitCode, stderr = stderr))

        // 正常終了していても入力を書き切れていないなら、出力は途中までの変換かもしれない。
        // 成功として返すと不完全なナレッジが静かに書き出されるため、書き込みの失敗を報告する。
        match writeError with
        | Some error -> raise error
        | None -> ()

        return markdown
    }

/// resolvePathで解決したpandocを指定した引数で起動してHTML文字列をMarkdownへ変換する。
/// エラー条件はtoMarkdownWithと同じ。
let toMarkdownWithArguments (arguments: string list) (html: string) : Task<string> =
    toMarkdownWith (resolvePath ()) arguments html

/// resolvePathで解決したpandocでHTML文字列をGFM Markdownへ変換する。
/// エラー条件はtoMarkdownWithと同じ。
let toMarkdown (html: string) : Task<string> =
    toMarkdownWithArguments defaultMarkdownArguments html
