module BluePrompt.Test.PandocSpec

open System.IO
open System.Threading.Tasks
open Falco.Markup
open Xunit

[<Fact>]
let ``h1タグはATX見出しに変換される`` () : Task =
    task {
        let! markdown = BluePrompt.Pandoc.toMarkdown (renderNode (Text.h1 "Example Domain"))
        Assert.Equal("# Example Domain", markdown.Trim())
    }

[<Fact>]
let ``長い段落は行折り返しされず1行になる`` () : Task =
    task {
        let sentence = String.replicate 30 "This paragraph is intentionally long. "
        let! markdown = BluePrompt.Pandoc.toMarkdown (renderNode (Text.p sentence))
        Assert.DoesNotContain("\n", markdown.Trim())
    }

[<Fact>]
let ``日本語などの非ASCII文字が化けずにMarkdownへ現れる`` () : Task =
    task {
        let! markdown = BluePrompt.Pandoc.toMarkdown (renderNode (Text.p "日本語テキストと絵文字🎉"))
        Assert.Contains("日本語テキストと絵文字🎉", markdown)
    }

[<Fact>]
let ``空のHTMLは空のMarkdownになる`` () : Task =
    task {
        let! markdown = BluePrompt.Pandoc.toMarkdown ""
        Assert.Equal("", markdown.Trim())
    }

[<Fact>]
let ``数MB規模のHTMLもハングせず変換できる`` () : Task =
    task {
        let html = String.replicate 100000 (renderNode (Text.p "large input"))
        let! markdown = BluePrompt.Pandoc.toMarkdown html
        Assert.Contains("large input", markdown)
    }

[<Fact>]
let ``変換できないタグの生HTMLは残らない`` () : Task =
    task {
        let html =
            renderNode (Elem.p [] [ Elem.span [ Attr.style "color: red" ] [ Text.raw "text" ] ])

        let! markdown = BluePrompt.Pandoc.toMarkdown html

        Assert.DoesNotContain("<span", markdown)
        Assert.Contains("text", markdown)
    }

[<Fact>]
let ``pandocが異常終了するとPandocErrorにexit codeとstderrが入る`` () : Task =
    task {
        // pandocの代わりにstderrへ出力して非0終了するスクリプトを使い、異常系を決定的に再現する。
        let script = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        do! File.WriteAllTextAsync(script, "#!/bin/sh\necho boom >&2\nexit 3\n")

        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        try
            let! error =
                Assert.ThrowsAsync<BluePrompt.Pandoc.PandocError>(fun () ->
                    BluePrompt.Pandoc.toMarkdownWith
                        script
                        BluePrompt.Pandoc.defaultMarkdownArguments
                        (renderNode (Text.h1 "x"))
                    :> Task)

            Assert.Equal(3, error.exitCode)
            Assert.Contains("boom", error.stderr)
        finally
            File.Delete script
    }

[<Fact>]
let ``入力を読まずに異常終了してもPandocErrorになる`` () : Task =
    task {
        // 入力を読まずに即終了するプロセスへの書き込みはbroken pipeになる。
        // それでも書き込みの失敗ではなくexit codeがPandocErrorとして報告されることを検証する。
        let script = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        do! File.WriteAllTextAsync(script, "#!/bin/sh\nexit 4\n")

        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        try
            // パイプバッファ(Linuxで既定64KiB)に収まらないサイズにして、
            // プロセス終了前に書き込みが完了してしまう取りこぼしを防ぐ。
            let html = String.replicate 100000 (renderNode (Text.p "x"))

            let! error =
                Assert.ThrowsAsync<BluePrompt.Pandoc.PandocError>(fun () ->
                    BluePrompt.Pandoc.toMarkdownWith
                        script
                        BluePrompt.Pandoc.defaultMarkdownArguments
                        html
                    :> Task)

            Assert.Equal(4, error.exitCode)
        finally
            File.Delete script
    }

[<Fact>]
let ``入力を読み切らずに正常終了した場合は成功扱いにしない`` () : Task =
    task {
        // 入力の一部だけ読んでexit code 0で終了するプロセスを用意する。
        // 書き込みがbroken pipeで失敗しているのに、
        // 途中までの出力が成功として返るとナレッジが静かに壊れるため、例外になることを検証する。
        let script = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        do! File.WriteAllTextAsync(script, "#!/bin/sh\nhead -c 100 > /dev/null\nexit 0\n")

        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        try
            // パイプバッファ(Linuxで既定64KiB)に収まらないサイズにして、
            // 書き込みが必ず途中で失敗する状況を作る。
            let html = String.replicate 100000 (renderNode (Text.p "x"))

            do!
                Assert.ThrowsAsync<IOException>(fun () ->
                    BluePrompt.Pandoc.toMarkdownWith
                        script
                        BluePrompt.Pandoc.defaultMarkdownArguments
                        html
                    :> Task)
                :> Task
        finally
            File.Delete script
    }

[<Fact>]
let ``引数を差し替えると変換先フォーマットを変えられる`` () : Task =
    task {
        let! rst =
            BluePrompt.Pandoc.toMarkdownWithArguments
                [ "-f"; "html"; "-t"; "rst"; "--wrap=none"; "--sandbox" ]
                (renderNode (Text.h1 "Example Domain"))

        // reStructuredTextの見出しはテキストの下線で表現される。
        Assert.Contains("Example Domain\n==============", rst)
    }

[<Fact>]
let ``tableはパイプテーブルに変換される`` () : Task =
    task {
        let html =
            renderNode (
                Elem.table
                    []
                    [ Elem.tr [] [ Elem.th [] [ Text.raw "name" ]; Elem.th [] [ Text.raw "value" ] ]
                      Elem.tr [] [ Elem.td [] [ Text.raw "foo" ]; Elem.td [] [ Text.raw "1" ] ] ]
            )

        let! markdown = BluePrompt.Pandoc.toMarkdown html
        // pandocはセル幅を空白で揃えるため、パディングに依存しない形で検証する。
        Assert.Contains("| name | value |", markdown)
        Assert.Matches(@"\| foo\s+\| 1\s+\|", markdown)
    }
