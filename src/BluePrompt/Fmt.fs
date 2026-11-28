/// nix fmtを起動して書き出したファイルを整形する。
module BluePrompt.Fmt

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open CliWrap
open CliWrap.Buffered

/// nix fmtが異常終了した時のexit codeとstderr。
exception FmtError of exitCode: int * stderr: string

/// nix fmtの完了をこれ以上待たない打ち切り時間。
/// 初回はtreefmt自体の評価やビルドが走ることがあるため、長めに取る。
let private timeout = TimeSpan.FromMinutes 5.

/// 指定したパスの列をnix fmtの1回の起動でまとめて整形する。
let private runFormat (fullPaths: string list) : Task<unit> =
    task {
        use cancellation = new CancellationTokenSource(timeout)

        let! result =
            task {
                try
                    return!
                        Cli
                            .Wrap("nix")
                            .WithArguments("fmt" :: "--" :: fullPaths)
                            .WithWorkingDirectory(Path.GetDirectoryName(List.head fullPaths))
                            // 終了コードはFmtErrorとして自分で報告するため、CliWrapの検査は使わない。
                            .WithValidation(CommandResultValidation.None)
                            .ExecuteBufferedAsync(Encoding.UTF8, cancellation.Token)
                            .Task
                // 打ち切ったプロセスはCliWrapが終了させる。
                with :? OperationCanceledException ->
                    return
                        raise (TimeoutException $"nix fmtが%.0f{timeout.TotalSeconds}秒以内に終了しませんでした")
            }

        if result.ExitCode <> 0 then
            raise (FmtError(exitCode = result.ExitCode, stderr = result.StandardError))
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
/// 打ち切り時間を超えた場合はTimeoutExceptionを送出する。
/// devShell外での実行などでnixが見つからない場合は、
/// 起動しようとしたパスをメッセージに持つWin32Exceptionがそのまま届く。
let formatFiles (paths: string list) : Task<unit> =
    match List.map Path.GetFullPath paths with
    | [] -> Task.FromResult()
    | fullPaths -> runFormat fullPaths

/// 単一のファイルをnix fmtで整形する。挙動と失敗条件はformatFilesと同じ。
let formatFile (path: string) : Task<unit> = formatFiles [ path ]
