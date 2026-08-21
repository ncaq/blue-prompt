module BluePrompt.Test.RolePlaySpec

open System
open System.IO
open Xunit
open BluePrompt.RolePlay

/// 参照ファイル1つ分のMarkdownを組み立てる。
/// wikiruの生徒個別ページから生成したものと同じ形にする。
let private reference (pageName: string) : string =
    let url = "https://bluearchive.wikiru.jp/?" + Uri.EscapeDataString pageName

    $"出典: [%s{pageName} - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{url})\n"
    + "\n## 基本情報\n\n| 名前 | ユウカ |\n\n## ボイス\n"

[<Fact>]
let ``parseReferenceはファイル名と出典のページ名を読む`` () =
    let parsed = parseReference "/skills/yuuka/normal.md" (reference "ユウカ")

    Assert.Equal("normal.md", parsed.FileName)
    Assert.Equal("ユウカ", parsed.PageName)

[<Fact>]
let ``parseReferenceはパーセントエンコードされたページ名を復元する`` () =
    Assert.Equal("ユウカ（体操服）", (parseReference "track.md" (reference "ユウカ（体操服）")).PageName)

[<Fact>]
let ``出典の行が無い参照ファイルはReferenceShapeErrorになる`` () =
    // 読めなかった衣装を黙って落とすと、一覧からその衣装が静かに消える。
    let error =
        Assert.Throws<ReferenceShapeError>(fun () ->
            parseReference "normal.md" "## 基本情報\n\n| 名前 | ユウカ |\n" |> ignore)

    match error :> exn with
    | ReferenceShapeError(path, missing) ->
        Assert.Equal("normal.md", path)
        Assert.Equal("出典の行", missing)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``ページ名を持たない出典はReferenceShapeErrorになる`` () =
    let markdown = "出典: [ユウカ - Wiki](https://bluearchive.wikiru.jp/)\n"

    let error =
        Assert.Throws<ReferenceShapeError>(fun () -> parseReference "normal.md" markdown |> ignore)

    match error :> exn with
    | ReferenceShapeError(_, missing) -> Assert.Equal("出典のページ名", missing)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``絶対URLではない出典はReferenceShapeErrorになる`` () =
    // 壊れたリンクをUriのコンストラクタに任せると、
    // どのファイルが壊れているのかがメッセージから消える。
    let markdown = "出典: [ユウカ - Wiki](./?ユウカ)\n"

    let error =
        Assert.Throws<ReferenceShapeError>(fun () -> parseReference "normal.md" markdown |> ignore)

    match error :> exn with
    | ReferenceShapeError(path, missing) ->
        Assert.Equal("normal.md", path)
        Assert.Equal("出典のURL", missing)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``toCostumeMarkdownはリンクと出典のページ名を並べる`` () =
    let costumes =
        [ parseReference "normal.md" (reference "ユウカ")
          parseReference "track.md" (reference "ユウカ（体操服）") ]

    Assert.Equal(
        "- [normal.md](./normal.md): ユウカ\n- [track.md](./track.md): ユウカ（体操服）",
        toCostumeMarkdown costumes
    )

/// 参照ファイルを並べた一時ディレクトリを作る。
let private makeDirectory (files: (string * string) list) : string =
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory directory |> ignore

    for name, content in files do
        File.WriteAllText(Path.Combine(directory, name), content)

    directory

[<Fact>]
let ``readReferencesは通常衣装を先頭にしてスキル本体を除く`` () =
    // MODEL.mdは実際に同じディレクトリへ生成されるので、
    // 除外が漏れると出典の行を持たない衣装としてReferenceShapeErrorになる。
    let directory =
        makeDirectory
            [ "SKILL.md", "生成物"
              "MODEL.md", "Model向けの本文"
              "character.md", "固有の手書き"
              "track.md", reference "ユウカ（体操服）"
              "pajama.md", reference "ユウカ（パジャマ）"
              "normal.md", reference "ユウカ" ]

    Assert.Equal<string list>(
        [ "normal.md"; "pajama.md"; "track.md" ],
        (readReferences directory).GetAwaiter().GetResult() |> List.map _.FileName
    )

[<Fact>]
let ``参照ファイルの無いディレクトリはReferenceNotFoundになる`` () =
    // 衣装の一覧が空のまま書き出されないようにする。
    let directory = makeDirectory [ "character.md", "固有の手書き" ]

    Assert.Throws<ReferenceNotFound>(fun () ->
        (readReferences directory).GetAwaiter().GetResult() |> ignore)
    |> ignore

[<Fact>]
let ``knowledgeSkillsMarkdownは生徒のナレッジのスキル名を並べる`` () =
    // 全てのスキルが参照するcharacter-appellationは、本文では別の文が扱うので除く。
    let names = [ "character-yuuka"; "character-yuuka-track"; "character-appellation" ]

    Assert.Equal(
        "- character-yuuka\n- character-yuuka-track",
        knowledgeSkillsMarkdown "character.md" names
    )

[<Fact>]
let ``生徒のナレッジが無いとKnowledgeSkillNotFoundになる`` () =
    // 参照先を挙げない壊れた文を書き出さない。
    let error =
        Assert.Throws<KnowledgeSkillNotFound>(fun () ->
            knowledgeSkillsMarkdown "character.md" [ "character-appellation" ] |> ignore)

    match error :> exn with
    | KnowledgeSkillNotFound path -> Assert.Equal("character.md", path)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"
