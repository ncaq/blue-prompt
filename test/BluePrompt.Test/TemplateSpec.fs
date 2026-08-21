module BluePrompt.Test.TemplateSpec

open Xunit
open BluePrompt.Template

/// テンプレートを組み立てるテストで、波括弧の数を自前で書かずに済むようにする。
/// 補間された文字列の中では引用符付きの文字列リテラルを書けないため、束縛して使う。
let private appellation = placeholder "appellation"

[<Fact>]
let ``placeholderは名前を波括弧で囲む`` () =
    Assert.Equal("{{appellation}}", appellation)

[<Fact>]
let ``プレースホルダへ値を差し込む`` () =
    let template = $"# 呼称\n\n前書き\n\n%s{appellation}\n\n# 次のセクション\n"

    Assert.Equal(
        Ok "# 呼称\n\n前書き\n\n| 相手 | 呼称 |\n\n# 次のセクション\n",
        render (Map [ "appellation", "| 相手 | 呼称 |" ]) template
    )

[<Fact>]
let ``複数のプレースホルダへそれぞれの値を差し込む`` () =
    // 差し込む箇所は今後増えるため、名前ごとに独立して埋まることを固定する。
    let voice = placeholder "voice"
    let template = $"%s{appellation}と%s{voice}\n"

    Assert.Equal(Ok "呼称と台詞\n", render (Map [ "appellation", "呼称"; "voice", "台詞" ]) template)

[<Fact>]
let ``同じプレースホルダが繰り返されると全てに差し込む`` () =
    let name = placeholder "name"
    Assert.Equal(Ok "ユウカ/ユウカ", render (Map [ "name", "ユウカ" ]) $"%s{name}/%s{name}")

[<Fact>]
let ``値の無いプレースホルダはUnresolvedになる`` () =
    // プレースホルダの書き間違いで、差し込むはずの内容が黙って落ちるのを防ぐ。
    let typo = placeholder "typo"

    Assert.Equal(
        Error { Unresolved = [ "typo" ]; Unused = [] },
        render (Map [ "appellation", "呼称" ]) $"%s{appellation}と%s{typo}"
    )

[<Fact>]
let ``テンプレートに無い名前へ渡した値はUnusedになる`` () =
    // テンプレートから消したプレースホルダへ値を渡し続けているのを見つける。
    Assert.Equal(
        Error
            { Unresolved = []
              Unused = [ "voice" ] },
        render (Map [ "voice", "台詞" ]) "本文だけのテンプレート"
    )

[<Fact>]
let ``食い違うテンプレートには差し込まない`` () =
    // 一部だけ差し込まれた中途半端な生成物を書き出さないため、
    // 名前の対応が取れているプレースホルダがあっても結果を返さない。
    let typo = placeholder "typo"
    let values = Map [ "appellation", "呼称"; "voice", "台詞" ]

    Assert.True(Result.isError (render values $"%s{appellation}と%s{typo}"))

[<Fact>]
let ``差し込む値の中の置換パターンはそのまま残る`` () =
    // 置換する文字列を渡すとRegexが`$1`を後方参照として解釈してしまう。
    Assert.Equal(Ok "$1と$&", render (Map [ "appellation", "$1と$&" ]) appellation)

[<Fact>]
let ``差し込んだ値の中のプレースホルダは展開されない`` () =
    // 走査の対象をテンプレートだけに留めて、値の中身が二重に解釈されないようにする。
    let voice = placeholder "voice"
    let values = Map [ "appellation", voice; "voice", "展開された" ]

    Assert.Equal(Ok "{{voice}} 展開された", render values $"%s{appellation} %s{voice}")

[<Fact>]
let ``renderOrFailはテンプレートへ差し込む`` () =
    Assert.Equal("呼称", renderOrFail "SKILL.template.md" (Map [ "appellation", "呼称" ]) appellation)

[<Fact>]
let ``renderOrFailは食い違ったテンプレートのパスを添えて止まる`` () =
    let error =
        Assert.Throws<PlaceholderMismatch>(fun () ->
            renderOrFail "SKILL.template.md" (Map [ "appellation", "呼称" ]) "本文だけ" |> ignore)

    match error :> exn with
    | PlaceholderMismatch(path, mismatch) ->
        Assert.Equal("SKILL.template.md", path)
        Assert.Equal<string list>([ "appellation" ], mismatch.Unused)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"
