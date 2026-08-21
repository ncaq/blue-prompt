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
let ``通常衣装が無ければファイル名の順に並ぶ`` () =
    let directory =
        makeDirectory [ "track.md", reference "ユウカ（体操服）"; "pajama.md", reference "ユウカ（パジャマ）" ]

    Assert.Equal<string list>(
        [ "pajama.md"; "track.md" ],
        (readReferences directory).GetAwaiter().GetResult() |> List.map _.FileName
    )

[<Fact>]
let ``衣装以外のMarkdownを置くとReferenceShapeErrorになる`` () =
    // 除外を並べる形なので、スキルのディレクトリへ別のMarkdownを置くと衣装と見なされる。
    let directory =
        makeDirectory [ "normal.md", reference "ユウカ"; "README.md", "# 説明\n" ]

    let error =
        Assert.Throws<ReferenceShapeError>(fun () ->
            (readReferences directory).GetAwaiter().GetResult() |> ignore)

    match error :> exn with
    | ReferenceShapeError(path, missing) ->
        Assert.Equal("README.md", Path.GetFileName path)
        Assert.Equal("出典の行", missing)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

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

/// テンプレートの全てのプレースホルダを、
/// 差し込まれた値だけが並ぶ形で書き出すテンプレート。
/// 配線の取り違えがそのまま出力の順序の違いとして現れる。
let private template =
    "{{caller}}\n{{character}}\n{{playing}}\n{{appellation}}\n{{costumes}}\n{{knowledgeSkills}}\n"

/// キャラ呼称表のページのURL。
let private appellationSource =
    "https://bluearchive.wikiru.jp/?" + Uri.EscapeDataString "キャラ呼称表"

/// 呼称表を1件だけ持つ文書を組み立てる。
let private document (caller: string) : BluePrompt.Appellation.Document =
    { Source = appellationSource
      Entries =
        [ { School = "ミレニアム"
            Club = Some "セミナー"
            Caller = caller
            Callee = "先生"
            CalleeNote = None
            Name = "先生"
            Note = None } ] }

[<Fact>]
let ``renderSkillはフロントマターと差し込んだ本文を並べる`` () =
    let character =
        BluePrompt.OpenWebui.parseFrontmatter
            "character.md"
            ("---\nname: yuuka\ndescription: Role-play as Yuuka\n"
             + "knowledge: character-yuuka, character-appellation\n---\n\n早瀬ユウカの位置付け。\n")

    let skill =
        renderSkill
            { Caller = "ユウカ"
              TemplatePath = "SKILL.template.md"
              Template = template
              CharacterPath = "character.md"
              Character = character
              References = [ parseReference "normal.md" (reference "ユウカ") ]
              Appellation = document "ユウカ" }

    Assert.Equal(
        "---\nname: yuuka\ndescription: Role-play as Yuuka\n"
        + "knowledge: character-yuuka, character-appellation\n---\n"
        + "\nユウカ\n早瀬ユウカの位置付け。\n"
        + playingRules
        + $"\n出典: [キャラ呼称表 - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{appellationSource})\n"
        + "\n| 相手 | 呼称 |\n| --- | --- |\n| 先生 | 先生 |\n\n"
        + "- [normal.md](./normal.md): ユウカ\n"
        + "- character-yuuka\n",
        skill
    )

[<Fact>]
let ``本文の無いcharacter.mdはCharacterBodyNotFoundになる`` () =
    // 参照ファイルとナレッジは0件で止まるのに本文だけ素通りすると、
    // その生徒の位置付けが黙って落ちた本文が生成される。
    let error =
        Assert.Throws<CharacterBodyNotFound>(fun () ->
            renderSkill
                { Caller = "ユウカ"
                  TemplatePath = "SKILL.template.md"
                  Template = template
                  CharacterPath = "character.md"
                  Character =
                    BluePrompt.OpenWebui.parseFrontmatter
                        "character.md"
                        "---\nname: yuuka\ndescription: d\nknowledge: character-yuuka\n---\n"
                  References = [ parseReference "normal.md" (reference "ユウカ") ]
                  Appellation = document "ユウカ" }
            |> ignore)

    match error :> exn with
    | CharacterBodyNotFound path -> Assert.Equal("character.md", path)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``renderSkillはテンプレートのプレースホルダが欠けると止まる`` () =
    // 差し込むはずの内容が黙って落ちないようにする。
    Assert.Throws<BluePrompt.Template.PlaceholderMismatch>(fun () ->
        renderSkill
            { Caller = "ユウカ"
              TemplatePath = "SKILL.template.md"
              Template = "{{caller}}\n"
              CharacterPath = "character.md"
              Character =
                BluePrompt.OpenWebui.parseFrontmatter
                    "character.md"
                    "---\nname: yuuka\ndescription: d\nknowledge: character-yuuka\n---\n\n本文\n"
              References = [ parseReference "normal.md" (reference "ユウカ") ]
              Appellation = document "ユウカ" }
        |> ignore)
    |> ignore
