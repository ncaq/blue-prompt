module BluePrompt.Test.OpenWebuiSpec

open System.IO
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

/// Model向けの本文。フロントマターもSKILL.mdと違う値にして、どちらが読まれたか見分ける。
let private modelMd =
    """---
name: yuuka-model
description: Model向けの説明。
knowledge: character-yuuka
---

Model向けの本文です。
"""

[<Fact>]
let ``MODEL.mdがあればフロントマターごとSKILL.mdより優先される`` () =
    // Claude CodeとOpen WebUIでは参照ファイルとナレッジの届き方が違うため、
    // 本文はそれぞれの言い方で別に用意する。
    // 本文だけでなくknowledgeの紐付けもMODEL.md側で決まる。
    // 紐付けが外れるとopen-webui syncが参照の外れたModelとして止まる。
    let directory = makeSkillDirectory [ "SKILL.md", skillMd; "MODEL.md", modelMd ]
    let form = buildModelForm directory

    Assert.Equal("Model向けの本文です。\n", form.Params.System)
    Assert.Equal("yuuka-model", form.Id)
    Assert.Equal("Model向けの説明。", form.Meta.Description)

    Assert.Equal<string list>(
        [ "character-yuuka" ],
        form.Meta.Knowledge |> Option.defaultValue [] |> List.map _.Name
    )

[<Fact>]
let ``SKILL.mdが無くてもMODEL.mdだけで組み立てられる`` () =
    let directory = makeSkillDirectory [ "MODEL.md", modelMd ]

    Assert.Equal("Model向けの本文です。\n", (buildModelForm directory).Params.System)

[<Fact>]
let ``本文がどちらも無いとSkillFormatErrorになる`` () =
    let directory = makeSkillDirectory [ "normal.md", "# 通常\n" ]

    Assert.Throws<SkillFormatError>(fun () -> buildModelForm directory |> ignore)
    |> ignore

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
let ``参照ファイルへのリンクが本文に残っているとSkillFormatErrorになる`` () =
    // Open WebUIには会話の途中でファイルを開く手段が無いため、
    // リンクを書いても読み手には開けない参照が残るだけになる。
    let body =
        """---
name: yuuka
description: desc
---

- [normal.md](./normal.md): 通常
"""

    let directory =
        makeSkillDirectory [ "SKILL.md", body; "normal.md", "# 通常\n\n制服姿。\n" ]

    Assert.Throws<SkillFormatError>(fun () -> buildModelForm directory |> ignore)
    |> ignore

[<Fact>]
let ``MODEL.mdだけにリンクが残っていてもSkillFormatErrorになる`` () =
    // role-playスキルを変換する時に読まれるのはMODEL.mdなので、
    // 検査がSKILL.mdを見ていると、実際に焼かれる本文のリンクを見逃す。
    // 本文を選ぶ判定と検査の対象が同じであることを、
    // リンクを持たないSKILL.mdと並べて固定する。
    let body =
        """---
name: yuuka
description: desc
---

- [normal.md](./normal.md): 通常
"""

    let directory =
        makeSkillDirectory [ "SKILL.md", skillMd; "MODEL.md", body; "normal.md", "# 通常\n\n制服姿。\n" ]

    Assert.Throws<SkillFormatError>(fun () -> buildModelForm directory |> ignore)
    |> ignore

[<Fact>]
let ``URLとアンカーのリンクは参照ファイルとして扱われない`` () =
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

/// 参照ファイルの解決をスキルディレクトリの上で試す。
/// 解決はKnowledgeの組み立てが使うもので、Modelの本文はリンクを持たない。
let private resolve (files: (string * string) list) (body: string) : (string * string) list =
    let directory = makeSkillDirectory files
    resolveReferences directory (Path.Combine(directory, "SKILL.md")) body

[<Fact>]
let ``サブディレクトリの参照ファイルも解決される`` () =
    Assert.Equal<string list>(
        [ "sub/data.md" ],
        resolve [ "sub/data.md", "# 入れ子\n\n中身。\n" ] "[data.md](./sub/data.md)"
        |> List.map fst
    )

[<Fact>]
let ``ディレクトリを遡る参照はSkillFormatErrorになる`` () =
    Assert.Throws<SkillFormatError>(fun () -> resolve [] "[outside.md](../outside.md)" |> ignore)
    |> ignore

[<Fact>]
let ``絶対パスの参照はSkillFormatErrorになる`` () =
    // Path.Combineは絶対パスを渡されると連結せずそれ自体を返すため、
    // `..`を含むかどうかだけを見る検査ではスキルディレクトリの外へ抜けられる。
    // 内容はKnowledgeとして外部へ送られる。
    Assert.Throws<SkillFormatError>(fun () -> resolve [] "[passwd](/etc/passwd)" |> ignore)
    |> ignore

[<Fact>]
let ``リンク先のファイルが存在しないとSkillFormatErrorになる`` () =
    Assert.Throws<SkillFormatError>(fun () -> resolve [] "[missing.md](./missing.md)" |> ignore)
    |> ignore

[<Fact>]
let ``フロントマターが無いとSkillFormatErrorになる`` () =
    let directory = makeSkillDirectory [ "SKILL.md", "本文だけ\n" ]

    Assert.Throws<SkillFormatError>(fun () -> buildModelForm directory |> ignore)
    |> ignore

[<Fact>]
let ``Rawはフロントマターを解釈せずそのまま返す`` () =
    // role-playスキルの本文はこれを生成物へ写すため、
    // このリポジトリが読まない項目を書き足しても落ちてはいけない。
    let content =
        "---\nname: yuuka\ndescription: Role-play as Yuuka\nunknown: 値\n---\n\n本文\n"

    let frontmatter = parseFrontmatter "character.md" content

    Assert.Equal(
        "---\nname: yuuka\ndescription: Role-play as Yuuka\nunknown: 値\n---",
        frontmatter.Raw
    )

    Assert.Equal("本文", frontmatter.Body)

[<Fact>]
let ``閉じの区切り行が無いとSkillFormatErrorになる`` () =
    // 開始だけを見て通すと、フロントマターの全体が本文として流れ込む。
    Assert.Throws<SkillFormatError>(fun () ->
        parseFrontmatter "character.md" "---\nname: yuuka\n\n本文\n" |> ignore)
    |> ignore

[<Fact>]
let ``CRLFのファイルでもフロントマターと本文が分かれる`` () =
    let content =
        "---\r\nname: yuuka\r\ndescription: Role-play as Yuuka\r\n---\r\n\r\n本文\r\n"

    let frontmatter = parseFrontmatter "character.md" content

    Assert.Equal("---\nname: yuuka\ndescription: Role-play as Yuuka\n---", frontmatter.Raw)
    Assert.Equal("本文", frontmatter.Body)
    Assert.Equal("yuuka", frontmatter.Name)

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
