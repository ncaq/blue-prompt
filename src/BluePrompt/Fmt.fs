/// nix fmtを起動して書き出したファイルを整形する。
module BluePrompt.Fmt

open System
open System.ComponentModel
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

/// 指定したパスの列をnix fmtの1回の起動でまとめて整形する。
let private runFormat (fullPaths: string list) : Task<unit> =
    task {
        let startInfo =
            ProcessStartInfo(
                FileName = "nix",
                WorkingDirectory = Path.GetDirectoryName(List.head fullPaths),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            )

        for argument in "fmt" :: "--" :: fullPaths do
            startInfo.ArgumentList.Add argument

        use cancellation = new CancellationTokenSource(timeout)

        let startFailedMessage = $"nixを起動できませんでした: %s{startInfo.FileName}"

        use fmt =
            // 実行ファイルが見つからない場合、StartはnullではなくWin32Exceptionを送出する。
            // devShell外での実行などを切り分けられるように、起動しようとしたパスを添えて包み直す。
            try
                match Process.Start startInfo with
                | null -> raise (InvalidOperationException startFailedMessage)
                | started -> started
            with :? Win32Exception as error ->
                raise (InvalidOperationException(startFailedMessage, error))
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

/// 指定したファイル群をnix fmtの1回の起動でまとめて整形する。
/// このリポジトリの生成物はコミット前にnix fmtが掛かる前提なので、
/// 書き出した直後に同じ整形を済ませて、生成コマンドだけで内容が確定するようにする。
/// nixの起動とtreefmtの評価が所要時間の支配項なので、
/// 複数ファイルを書き出すコマンドは1回の呼び出しへまとめる。
/// nixはカレントディレクトリから上へflake.nixを探すため、
/// どこから起動されても先頭のファイルのあるリポジトリのフォーマッタが使われるように、
/// 先頭のファイルのあるディレクトリを作業ディレクトリにする。
/// 空のリストは何もしない。
/// nix fmtが非0終了した場合はFmtErrorを、
/// 打ち切り時間を超えた場合はTimeoutExceptionを、
/// devShell外での実行などでnixが見つからない場合はInvalidOperationExceptionを送出する。
let formatFiles (paths: string list) : Task<unit> =
    match List.map Path.GetFullPath paths with
    | [] -> Task.FromResult()
    | fullPaths -> runFormat fullPaths

/// 単一のファイルをnix fmtで整形する。挙動と失敗条件はformatFilesと同じ。
let formatFile (path: string) : Task<unit> = formatFiles [ path ]
