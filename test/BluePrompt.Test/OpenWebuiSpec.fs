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
        File.WriteAllText(Path.Combine(directory, fileName), content)

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
