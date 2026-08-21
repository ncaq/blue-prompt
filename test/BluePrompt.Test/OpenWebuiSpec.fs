module BluePrompt.Test.OpenWebuiSpec

open System.IO
open System.Text.RegularExpressions
open Xunit
open BluePrompt.OpenWebui

/// スキルディレクトリをテスト用の一時ディレクトリへ組み立てる。
let private makeSkillDirectory (files: (string * string) list) : string =
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory directory |> ignore

    for fileName, content in files do
        let path = Path.Combine(directory, fileName)

        match Path.GetDirectoryName path with
        | null
        | "" -> ()
        | parent -> Directory.CreateDirectory parent |> ignore

        File.WriteAllText(path, content)

    directory

let private skillMd =
    """---
name: yuuka
description: Role-play as 早瀬ユウカ.
---

あなたは早瀬ユウカとして振る舞います。
"""

[<Fact>]
let ``フロントマターのnameとdescriptionがModelFormへ対応付く`` () =
    let directory = makeSkillDirectory [ "SKILL.md", skillMd ]
    let form = buildModelForm directory

    Assert.Equal("yuuka", form.Id)
    Assert.Equal("yuuka", form.Name)
    Assert.Equal("Role-play as 早瀬ユウカ.", form.Meta.Description)
    Assert.Equal(None, form.BaseModelId)
    Assert.True form.IsActive
    Assert.Equal("あなたは早瀬ユウカとして振る舞います。\n", form.Params.System)

[<Fact>]
let ``リンクされた参照ファイルはシステムプロンプトへインライン化される`` () =
    let body =
        """---
name: yuuka
description: desc
---

- [normal.md](./normal.md): 通常
- [normal.md](./normal.md): 重複したリンクは1回だけ展開される
"""

    let directory =
        makeSkillDirectory [ "SKILL.md", body; "normal.md", "# 通常\n\n制服姿。\n" ]

    let system = (buildModelForm directory).Params.System

    Assert.Contains("# 参照ファイル: normal.md", system)
    Assert.Contains("制服姿。", system)
    Assert.Equal(1, Regex.Matches(system, "参照ファイル: normal.md").Count)

[<Fact>]
let ``URLとアンカーのリンクはインライン化の対象にならない`` () =
    Assert.Equal<string list>(
        [ "normal.md" ],
        localLinkTargets "[wiki](https://example.com/?page) [節](#anchor) [normal.md](./normal.md)"
    )

[<Fact>]
let ``ドットスラッシュ無しの相対リンクも参照ファイルとして扱われる`` () =
    Assert.Equal<string list>([ "reference.md" ], localLinkTargets "[reference.md](reference.md)")
    // ./有りと無しは同じファイルなので1つにまとまる。
    Assert.Equal<string list>(
        [ "reference.md" ],
        localLinkTargets "[a](reference.md) [b](./reference.md)"
    )

[<Fact>]
let ``サブディレクトリの参照ファイルもインライン化される`` () =
    let body =
        """---
name: nested
description: desc
---

[data.md](./sub/data.md)
"""

    let directory =
        makeSkillDirectory [ "SKILL.md", body; "sub/data.md", "# 入れ子\n\n中身。\n" ]

    let system = (buildModelForm directory).Params.System

    Assert.Contains("# 参照ファイル: sub/data.md", system)
    Assert.Contains("中身。", system)

[<Fact>]
let ``ディレクトリを遡る参照はSkillFormatErrorになる`` () =
    let body =
        """---
name: escape
description: desc
---

[outside.md](../outside.md)
"""

    let directory = makeSkillDirectory [ "SKILL.md", body ]

    Assert.Throws<SkillFormatError>(fun () -> buildModelForm directory |> ignore)
    |> ignore

[<Fact>]
let ``Markdown以外の参照ファイルは拡張子を言語タグにしたコードブロックで包まれる`` () =
    let body =
        """---
name: appellation
description: desc
---

[appellation.json](./appellation.json)を使ってください。
"""

    let directory =
        makeSkillDirectory [ "SKILL.md", body; "appellation.json", """{ "entries": [] }""" ]

    let system = (buildModelForm directory).Params.System

    Assert.Contains("```json\n{ \"entries\": [] }\n```", system)

[<Fact>]
let ``参照ファイルがコードフェンスを含んでいてもより長いフェンスで包まれる`` () =
    let body =
        """---
name: fenced
description: desc
---

[data.txt](./data.txt)
"""

    let directory =
        makeSkillDirectory [ "SKILL.md", body; "data.txt", "```console\njq .\n```" ]

    let system = (buildModelForm directory).Params.System

    Assert.Contains("````txt\n```console\njq .\n```\n````", system)

[<Fact>]
let ``リンク先のファイルが存在しないとSkillFormatErrorになる`` () =
    let body =
        """---
name: broken
description: desc
---

[missing.md](./missing.md)
"""

    let directory = makeSkillDirectory [ "SKILL.md", body ]

    Assert.Throws<SkillFormatError>(fun () -> buildModelForm directory |> ignore)
    |> ignore

[<Fact>]
let ``フロントマターが無いとSkillFormatErrorになる`` () =
    let directory = makeSkillDirectory [ "SKILL.md", "本文だけ\n" ]

    Assert.Throws<SkillFormatError>(fun () -> buildModelForm directory |> ignore)
    |> ignore

[<Fact>]
let ``ModelFormはsnake_caseのキーでJSONへ直列化される`` () =
    let directory = makeSkillDirectory [ "SKILL.md", skillMd ]
    let json = toJson (buildModelForm directory)

    Assert.Contains("\"base_model_id\": null", json)
    Assert.Contains("\"is_active\": true", json)
    Assert.Contains("\"system\": \"あなたは早瀬ユウカとして振る舞います。\\n\"", json)

[<Fact>]
let ``フロントマターのknowledgeがModelの紐付けへ対応付く`` () =
    let body =
        """---
name: yuuka
description: desc
knowledge: character-yuuka, character-appellation
---

あなたは早瀬ユウカとして振る舞います。
"""

    let form = buildModelForm (makeSkillDirectory [ "SKILL.md", body ])

    // idとnameはModelFormにも同じ名前のフィールドがあるため、型を明示して取り違えを防ぐ。
    let knowledge: KnowledgeReference list = Option.defaultValue [] form.Meta.Knowledge

    Assert.Equal<string list>(
        [ "character-yuuka"; "character-appellation" ],
        knowledge |> List.map (fun reference -> reference.Name)
    )

    // typeがcollectionの項目はファイルのアクセス権検証を通らずに紐付く。
    Assert.True(knowledge |> List.forall (fun reference -> reference.Type = collectionType))
    // idは登録先のインスタンスが採番するため、生成の時点では空にしておく。
    Assert.True(knowledge |> List.forall (fun reference -> reference.Id = None))
    // 紐付けたKnowledgeを自動で参照させるにはツール呼び出しの方式の指定が要る。
    Assert.Equal(Some legacyFunctionCalling, form.Params.FunctionCalling)

[<Fact>]
let ``knowledgeを書かないスキルでは紐付けも方式の指定もされない`` () =
    let form = buildModelForm (makeSkillDirectory [ "SKILL.md", skillMd ])

    Assert.Equal(None, form.Meta.Knowledge)
    // このリポジトリと関係のない理由で選ばれた設定を上書きしないようにする。
    Assert.Equal(None, form.Params.FunctionCalling)

[<Fact>]
let ``管理対象へ後から足したフィールドが無い応答も読み戻せる`` () =
    // function_callingを導入する前に登録したModelの応答を模す。
    // 実際にOpen WebUIへ登録済みのModelのparamsにはsystemしかない。
    //
    // optionalFieldPathsの安全網として働くように、
    // 管理対象のoption型のフィールドは1つも書かない。
    // nullで明示すると、補う処理を落としてもこのテストが通ってしまう。
    let json =
        """{
  "id": "yuuka",
  "name": "yuuka",
  "meta": { "description": "説明", "profile_image_url": "/static/favicon.png" },
  "params": { "system": "プロンプト" },
  "is_active": true
}"""

    let form = ofJson "登録済みのModelの応答" json

    Assert.Equal("yuuka", form.Id)
    Assert.Equal(None, form.BaseModelId)
    Assert.Equal(None, form.Params.FunctionCalling)
    Assert.Equal(None, form.Meta.Knowledge)
