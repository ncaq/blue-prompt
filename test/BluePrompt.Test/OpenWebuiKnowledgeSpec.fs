module BluePrompt.Test.OpenWebuiKnowledgeSpec

open System.IO
open System.Text
open Xunit
open BluePrompt.OpenWebuiKnowledge

/// スキルディレクトリをテスト用の一時ディレクトリへ組み立てる。
let private makeSkillDirectory (files: (string * string) list) : string =
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory directory |> ignore

    for fileName, content in files do
        File.WriteAllText(Path.Combine(directory, fileName), content)

    directory

let private skillMd =
    """---
name: character-appellation
description: 呼称の一覧を引くスキル。
---

呼び方を調べるためのスキルです。

- [reference.md](./reference.md): 呼称一覧
- [appellation.json](./appellation.json): 機械読み出し用JSON
"""

[<Fact>]
let ``フロントマターがKnowledgeFormへ対応付く`` () =
    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", "## 学校\n\n中身。\n"
              "appellation.json", "{}" ]

    let knowledge = buildKnowledge directory

    Assert.Equal("character-appellation", knowledge.Form.Name)
    Assert.Equal("呼称の一覧を引くスキル。", knowledge.Form.Description)

[<Fact>]
let ``SKILL_mdとMarkdownの参照ファイルがファイルになる`` () =
    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", "## 学校\n\n中身。\n"
              "appellation.json", "{}" ]

    let fileNames = (buildKnowledge directory).Files |> List.map _.FileName

    Assert.Equal<string list>(
        [ "character-appellation-SKILL.md"; "character-appellation-reference.md" ],
        fileNames
    )

[<Fact>]
let ``Markdown以外の参照ファイルはKnowledgeへ含めない`` () =
    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", "## 学校\n\n中身。\n"
              "appellation.json", """{"entries":[]}""" ]

    let contents = (buildKnowledge directory).Files |> List.map _.Content

    // jqで引くためのJSONはOpen WebUIでは実行する主体がおらず、埋め込んでも役に立たない。
    Assert.DoesNotContain(contents, fun content -> content.Contains "entries")

[<Fact>]
let ``フロントマターはKnowledgeのファイルへ持ち込まない`` () =
    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", "## 学校\n\n中身。\n"
              "appellation.json", "{}" ]

    let skill =
        (buildKnowledge directory).Files
        |> List.find (fun file -> file.FileName = "character-appellation-SKILL.md")

    Assert.DoesNotContain("description:", skill.Content)
    Assert.StartsWith("呼び方を調べるためのスキルです。", skill.Content)

/// 単体では分割の上限に収まり、2つ並べると超える大きさの節を作る。
/// 上限の値を変えてもテストの意図が保たれるように、定数から大きさを導く。
let private section (name: string) =
    let line = "呼称の行。\n"

    let lineCount = maxFragmentBytes * 3 / 4 / Encoding.UTF8.GetByteCount line

    $"## %s{name}\n\n" + String.replicate lineCount line

[<Fact>]
let ``大きな参照ファイルは見出しの単位のファイルへ分かれる`` () =
    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", section "学校A" + section "学校B"
              "appellation.json", "{}" ]

    let fileNames = (buildKnowledge directory).Files |> List.map _.FileName

    Assert.Equal<string list>(
        [ "character-appellation-SKILL.md"
          "character-appellation-reference-学校A.md"
          "character-appellation-reference-学校B.md" ],
        fileNames
    )

[<Fact>]
let ``同じ見出しが繰り返されてもファイル名は衝突しない`` () =
    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", section "同じ名前" + section "同じ名前"
              "appellation.json", "{}" ]

    let fileNames = (buildKnowledge directory).Files |> List.map _.FileName

    Assert.Equal<string list>(
        [ "character-appellation-SKILL.md"
          "character-appellation-reference-同じ名前.md"
          "character-appellation-reference-同じ名前-2.md" ],
        fileNames
    )

[<Fact>]
let ``ファイル名に使えない文字は見出しから落とされる`` () =
    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", section "対策委員会/補習" + section "普通"
              "appellation.json", "{}" ]

    let fileNames = (buildKnowledge directory).Files |> List.map _.FileName

    Assert.Contains("character-appellation-reference-対策委員会-補習.md", fileNames)

[<Fact>]
let ``ファイル名に使えない文字はコレクションの名前からも落とされる`` () =
    // `toFileNameBase`は見出しだけをサニタイズしていて、
    // コレクションの名前と参照ファイル名から作る先頭部分は素通りする。
    // どちらもフロントマターとリンクという入力に由来するため、
    // 区切り文字が混ざると生成物のディレクトリの外へ書き出せてしまう。
    let skill =
        """---
name: ../../escape
description: 外へ出るスキル。
---

呼び方を調べるためのスキルです。
"""

    let directory = makeSkillDirectory [ "SKILL.md", skill ]

    let fileNames = (buildKnowledge directory).Files |> List.map _.FileName

    // 書き出しはPath.Combineへ渡されるため、名前がそのままファイル名でなければならない。
    Assert.All(fileNames, fun fileName -> Assert.Equal(fileName, Path.GetFileName fileName))

[<Fact>]
let ``KnowledgeFormはJSONへ往復できる`` () =
    let form =
        { Name = "character-appellation"
          Description = "呼称の一覧を引くスキル。" }

    Assert.Equal(form, ofJson (toJson form))

[<Fact>]
let ``出典の行は全ての断片へ配られる`` () =
    let source = "出典: [ユウカ（体操服） - ブルーアーカイブ(ブルアカ)攻略有志Wiki](https://example.com/yuuka)"

    let directory =
        makeSkillDirectory
            [ "SKILL.md", skillMd
              "reference.md", $"%s{source}\n\n" + section "学校A" + section "学校B"
              "appellation.json", "{}" ]

    let fragments =
        (buildKnowledge directory).Files
        |> List.filter (fun file -> file.FileName.Contains "reference")

    // 記事名が本文に入ることで、衣装違いのような似た文書を検索で区別できる。
    Assert.All(fragments, fun file -> Assert.StartsWith(source, file.Content))
    // 出典の行だけの短い断片は、検索結果の枠を実データから奪うので作らない。
    Assert.Equal<string list>(
        [ "character-appellation-reference-学校A.md"
          "character-appellation-reference-学校B.md" ],
        fragments |> List.map _.FileName
    )
