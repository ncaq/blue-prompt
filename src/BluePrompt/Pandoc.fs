/// pandoc CLIを起動してHTMLをMarkdownへ変換する。
module BluePrompt.Pandoc

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open CliWrap
open CliWrap.Buffered

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
let toMarkdownWith (pandocPath: string) (arguments: string list) (html: string) : Task<string> =
    task {
        use cancellation = new CancellationTokenSource(timeout)

        let! result =
            task {
                try
                    return!
                        Cli
                            .Wrap(pandocPath)
                            .WithArguments(arguments)
                            // Encoding.UTF8で符号化するとpandocへの入力に余計なBOMが混ざるため、BOMなしを明示する。
                            .WithStandardInputPipe(PipeSource.FromString(html, UTF8Encoding false))
                            // 終了コードはPandocErrorとして自分で報告するため、CliWrapの検査は使わない。
                            .WithValidation(CommandResultValidation.None)
                            .ExecuteBufferedAsync(Encoding.UTF8, cancellation.Token)
                            .Task
                // 打ち切ったプロセスはCliWrapが終了させる。
                with :? OperationCanceledException ->
                    return
                        raise (TimeoutException $"pandocが%.0f{timeout.TotalSeconds}秒以内に終了しませんでした")
            }

        if result.ExitCode <> 0 then
            raise (PandocError(exitCode = result.ExitCode, stderr = result.StandardError))

        return result.StandardOutput
    }

/// resolvePathで解決したpandocを指定した引数で起動してHTML文字列をMarkdownへ変換する。
/// エラー条件はtoMarkdownWithと同じ。
let toMarkdownWithArguments (arguments: string list) (html: string) : Task<string> =
    toMarkdownWith (resolvePath ()) arguments html

/// resolvePathで解決したpandocでHTML文字列をGFM Markdownへ変換する。
/// エラー条件はtoMarkdownWithと同じ。
let toMarkdown (html: string) : Task<string> =
    toMarkdownWithArguments defaultMarkdownArguments html
