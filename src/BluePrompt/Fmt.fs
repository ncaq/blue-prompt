/// nix fmtを起動して書き出したファイルを整形する。
module BluePrompt.Fmt

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// nix fmtが異常終了した時のexit codeとstderr。
exception FmtError of exitCode: int * stderr: string

/// nix fmtの完了をこれ以上待たない打ち切り時間。
/// 初回はtreefmt自体の評価やビルドが走ることがあるため、長めに取る。
let private timeout = TimeSpan.FromMinutes 5.

/// 指定したファイルをnix fmtで整形する。
/// このリポジトリの生成物はコミット前にnix fmtが掛かる前提なので、
/// 書き出した直後に同じ整形を済ませて、生成コマンドだけで内容が確定するようにする。
/// nixはカレントディレクトリから上へflake.nixを探すため、
/// どこから起動されてもファイルのあるリポジトリのフォーマッタが使われるように、
/// ファイルのあるディレクトリを作業ディレクトリにする。
/// nix fmtが非0終了した場合はFmtErrorを、
/// 打ち切り時間を超えた場合はTimeoutExceptionを送出する。
let formatFile (path: string) : Task<unit> =
    task {
        let fullPath = Path.GetFullPath path

        let startInfo =
            ProcessStartInfo(
                FileName = "nix",
                WorkingDirectory = Path.GetDirectoryName fullPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            )

        for argument in [ "fmt"; "--"; fullPath ] do
            startInfo.ArgumentList.Add argument

        use cancellation = new CancellationTokenSource(timeout)

        use fmt =
            // 実行ファイルが見つからない場合、StartはnullではなくWin32Exceptionを送出する。
            // devShell外での実行などを切り分けられるように、起動しようとしたパスを添えて包み直す。
            try
                match Process.Start startInfo with
                | null -> failwith $"nixを起動できませんでした: %s{startInfo.FileName}"
                | started -> started
            with :? System.ComponentModel.Win32Exception as error ->
                raise (InvalidOperationException($"nixを起動できませんでした: %s{startInfo.FileName}", error))
        // パイプバッファが満杯になるとプロセスと相互待ちになるため、
        // 終了待ちより先に標準出力と標準エラーの読み取りを開始しておく。
        let stdoutTask = fmt.StandardOutput.ReadToEndAsync()
        let stderrTask = fmt.StandardError.ReadToEndAsync()

        try
            do! fmt.WaitForExitAsync cancellation.Token
        with :? OperationCanceledException ->
            // 打ち切り時はプロセスを回収しないとパイプごとリークする。
            fmt.Kill(entireProcessTree = true)
            raise (TimeoutException $"nix fmtが%.0f{timeout.TotalSeconds}秒以内に終了しませんでした")

        let! _ = stdoutTask
        let! stderr = stderrTask

        if fmt.ExitCode <> 0 then
            raise (FmtError(exitCode = fmt.ExitCode, stderr = stderr))
    }
