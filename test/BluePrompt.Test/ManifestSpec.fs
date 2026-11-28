module BluePrompt.Test.ManifestSpec

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open BluePrompt

[<Fact>]
let ``runBoundedは同時に進める数をdegreeに収める`` () =
    task {
        let degree = 2
        let inFlight = ref 0
        let maxInFlight = ref 0
        // 全ての処理をここで止めておくことで、
        // 同時に動き始めたものは全て動いたままになり、最大の同時実行数をそのまま観測できる。
        let release = TaskCompletionSource()
        let reachedDegree = TaskCompletionSource()

        // 最大値は複数の処理が同時に更新するため、排他して大きい値だけを残す。
        let record (current: int) =
            lock maxInFlight (fun () ->
                if maxInFlight.Value < current then
                    maxInFlight.Value <- current)

        let work (index: int) () =
            task {
                let current = Interlocked.Increment inFlight
                record current

                if current = degree then
                    reachedDegree.TrySetResult() |> ignore

                do! release.Task
                Interlocked.Decrement inFlight |> ignore
                return index
            }

        let running =
            Manifest.runBounded degree (List.init 5 (fun index -> $"work%d{index}", work index))

        // 直列化する方向へ退行するとdegreeに到達せず永久に待つため、上限を付けてハングではなく失敗にする。
        // xUnitはFactに既定のタイムアウトを持たない。
        let! first = Task.WhenAny(reachedDegree.Task, Task.Delay(TimeSpan.FromMinutes 5.))
        let reached = Object.ReferenceEquals(first, reachedDegree.Task)
        // 到達していなくても止めていた処理を解放して、後続の待ちを残さない。
        release.SetResult()
        Assert.True(reached, $"同時実行数が%d{degree}に到達しませんでした")
        let! results = running

        Assert.Equal(degree, maxInFlight.Value)
        // 結果は終わった順ではなく入力の並びで返る。
        Assert.Equal<int list>(List.init 5 id, results)
    }

[<Fact>]
let ``runBoundedは失敗した処理の名前を添えてGenerationFailedを送出する`` () =
    task {
        let error = InvalidOperationException "失敗"

        let works =
            [ "ok", (fun () -> Task.FromResult 1)
              "failed", (fun () -> Task.FromException<int> error) ]

        let! raised =
            Assert.ThrowsAsync<Manifest.GenerationFailed>(fun () ->
                Manifest.runBounded 16 works :> Task)

        Assert.Equal("failed", raised.name)
        Assert.Same(error, raised.error)
    }

/// マニフェストのwikiru対象が書き出すパスの一覧。
let private wikiruOutputs =
    Manifest.wikiruTargets
    |> List.collect (fun target ->
        match target with
        | Target.Appellation(_, markdownOutput, jsonOutput)
        | Target.School(_, markdownOutput, jsonOutput) -> [ markdownOutput; jsonOutput ]
        | Target.Knowledge(_, output)
        | Target.StudentSkill(_, output)
        | Target.UnplayableSkill(_, output) -> [ output ])

[<Fact>]
let ``マニフェストの出力先は重複しない`` () =
    Assert.Equal<string list>(List.distinct wikiruOutputs, wikiruOutputs)

// 走査して該当があれば検査する形だと対象が0件でも通ってしまうため、
// 先に絞り込んで空でないことを確かめてから検査する。

[<Fact>]
let ``生徒スキルの出力先はSKILL.md`` () =
    let outputs =
        Manifest.wikiruTargets
        |> List.choose (function
            | Target.StudentSkill(_, output) -> Some output
            | _ -> None)

    Assert.NotEmpty outputs

    for output in outputs do
        Assert.Equal(SkillFile.skill, Path.GetFileName output)

[<Fact>]
let ``プレイアブルではない登場人物のスキルの出力先はSKILL.md`` () =
    let outputs =
        Manifest.wikiruTargets
        |> List.choose (function
            | Target.UnplayableSkill(_, output) -> Some output
            | _ -> None)

    Assert.NotEmpty outputs

    for output in outputs do
        Assert.Equal(SkillFile.skill, Path.GetFileName output)

[<Fact>]
let ``プレイアブルではない登場人物は生徒と同じページを持たない`` () =
    // 実装されてstudentsへ移した時にunplayablesから外し忘れると、同じ人物のスキルが2つ並ぶ。
    let studentPages = Manifest.students |> List.map _.Page |> Set.ofList

    Assert.NotEmpty Manifest.unplayables

    for unplayable in Manifest.unplayables do
        Assert.DoesNotContain(unplayable.Page, studentPages)

[<Fact>]
let ``role-playスキルが読む呼称表はwikiruの対象が書き出す`` () =
    Assert.NotEmpty Manifest.rolePlaySkills

    for skill in Manifest.rolePlaySkills do
        Assert.Contains(skill.Appellation, wikiruOutputs)

[<Fact>]
let ``role-playスキルが読む衣装は生徒個別スキルと同じディレクトリに書き出される`` () =
    // 衣装の型のJSONは生徒個別スキルがSKILL.mdと一緒に書き出すため、
    // 読む場所がずれると衣装が読めずに生成が止まる。
    let skillDirectories =
        Manifest.wikiruTargets
        |> List.choose (function
            | Target.StudentSkill(_, output) -> Some(Path.GetDirectoryName output)
            | _ -> None)

    Assert.NotEmpty Manifest.rolePlaySkills

    for skill in Manifest.rolePlaySkills do
        Assert.NotEmpty skill.Costumes

        for costume in skill.Costumes do
            Assert.Equal("character.json", Path.GetFileName costume)
            Assert.Contains(Path.GetDirectoryName costume, skillDirectories)

[<Fact>]
let ``role-playスキルの衣装は素の衣装が先頭に来る`` () =
    let yuuka = Manifest.rolePlaySkills |> List.find (fun skill -> skill.Caller = "ユウカ")

    Assert.Equal(
        "plugins/jp-wikiru-bluearchive/skills/character-yuuka/character.json",
        List.head yuuka.Costumes
    )

    Assert.True(List.length yuuka.Costumes > 1)

[<Fact>]
let ``生徒個別スキルとプレイアブルではない登場人物のスキルの名前は出力先のディレクトリの名前`` () =
    let names =
        Manifest.characterSkillNames
            [ Target.StudentSkill("ユウカ", "plugins/skills/character-yuuka/SKILL.md")
              Target.UnplayableSkill("ユメ", "plugins/skills/character-yume/SKILL.md")
              Target.Knowledge("ページ", "plugins/skills/other/reference.md") ]

    Assert.Equal<Set<string>>(Set.ofList [ "character-yuuka"; "character-yume" ], names)

[<Fact>]
let ``ナレッジの名前が生徒個別スキルとして実在すれば空`` () =
    // 呼称表は生徒個別ではないので、実在しない名前として数えない。
    let missing =
        Manifest.missingKnowledgeSkills
            (Set.ofList [ "character-kotori"; "character-kotori-cheer-squad" ])
            [ "kotori/character.md",
              [ "character-kotori"; "character-kotori-cheer-squad"; "character-appellation" ] ]

    Assert.Empty missing

[<Fact>]
let ``実在しないナレッジの名前はcharacter.mdごとに集まる`` () =
    // 綴りの間違いも、生徒個別スキルの生成対象の足し忘れも、この形で現れる。
    let missing =
        Manifest.missingKnowledgeSkills
            (Set.ofList [ "character-kotori" ])
            [ "kotori/character.md", [ "character-kotori"; "character-kotori-cheer-squid" ]
              "yuuka/character.md", [ "character-yuuka" ] ]

    Assert.Equal<(string * string list) list>(
        [ "kotori/character.md", [ "character-kotori-cheer-squid" ]
          "yuuka/character.md", [ "character-yuuka" ] ],
        missing
    )

/// 学校別キャラクター一覧の1件。突き合わせが見るのはレアリティと名前とページ名だけ。
let private schoolEntry (rarity: string) (name: string) (page: string option) : School.Entry =
    { School = "学校"
      Club = None
      Rarity = rarity
      Name = name
      Page = page }

[<Fact>]
let ``一覧とマニフェストが揃っていれば食い違いは無い`` () =
    let coverage =
        Manifest.coverageOf
            [ "アズサ"; "アズサ（水着）" ]
            [ "ユメ" ]
            [ "アズサ"; "アズサ（水着）" ]
            [ schoolEntry "★3" "アズサ" None; schoolEntry "NPC" "ユメ" None ]

    Assert.False(Manifest.hasGap coverage)
    Assert.Empty coverage.Upcoming

[<Fact>]
let ``キャラクター一覧にだけある衣装違いは生徒の漏れになる`` () =
    // 衣装違いは学校別キャラクター一覧に現れないため、キャラクター一覧からしか見つけられない。
    let coverage =
        Manifest.coverageOf [ "アズサ" ] [] [ "アズサ"; "アズサ（レーシング）" ] [ schoolEntry "★3" "アズサ" None ]

    Assert.Equal<string list>([ "アズサ（レーシング）" ], coverage.MissingStudents)
    Assert.True(Manifest.hasGap coverage)

[<Fact>]
let ``実装された登場人物は生徒の漏れとunplayablesの外し忘れの両方に現れる`` () =
    let coverage =
        Manifest.coverageOf [] [ "ラナ" ] [ "ラナ" ] [ schoolEntry "★1" "ラナ" None ]

    Assert.Equal<string list>([ "ラナ" ], coverage.MissingStudents)
    Assert.Equal<string list>([ "ラナ" ], coverage.UnknownUnplayables)
    // キャラクター一覧に載っているので、実装前としては数えない。
    Assert.Empty coverage.Upcoming

[<Fact>]
let ``名前と食い違うページ名を持つ登場人物はページ名で突き合わせる`` () =
    // カイテンジャーの5人は同じページを指すので、1つのページとして数える。
    let coverage =
        Manifest.coverageOf
            []
            []
            []
            [ schoolEntry "NPC" "カイテンレッド" (Some "カイテンジャー")
              schoolEntry "NPC" "カイテンブラック" (Some "カイテンジャー") ]

    Assert.Equal<string list>([ "カイテンジャー" ], coverage.MissingUnplayables)

[<Fact>]
let ``一覧に無いマニフェストのページは食い違いになる`` () =
    let coverage =
        Manifest.coverageOf
            [ "アズサ"; "アズザ" ]
            [ "ユメ"; "ユヌ" ]
            [ "アズサ" ]
            [ schoolEntry "NPC" "ユメ" None ]

    Assert.Equal<string list>([ "アズザ" ], coverage.UnknownStudents)
    Assert.Equal<string list>([ "ユヌ" ], coverage.UnknownUnplayables)

[<Fact>]
let ``学校別キャラクター一覧にだけある生徒は実装前として扱い漏れにしない`` () =
    let coverage =
        Manifest.coverageOf
            [ "エリ" ]
            []
            [ "エリ" ]
            [ schoolEntry "★3" "エリ" None; schoolEntry "★3" "マナミ" None ]

    Assert.Equal<string list>([ "マナミ" ], coverage.Upcoming)
    Assert.False(Manifest.hasGap coverage)

[<Fact>]
let ``対象のパスはルートからの相対として解決される`` () =
    let root = Path.Combine("repo", "root")

    Assert.Equal(
        Target.Knowledge("ページ", Path.Combine(root, "out.md")),
        Target.resolveWikiru root (Target.Knowledge("ページ", "out.md"))
    )

    let skill: Target.RolePlaySkill =
        { Caller = "ユウカ"
          Template = "template"
          Appellation = "appellation.json"
          Costumes = [ "skills/character-yuuka/character.json" ]
          Output = "skills/yuuka" }

    Assert.Equal(
        { skill with
            Template = Path.Combine(root, "template")
            Appellation = Path.Combine(root, "appellation.json")
            Costumes = [ Path.Combine(root, "skills/character-yuuka/character.json") ]
            Output = Path.Combine(root, "skills/yuuka") },
        Target.resolveRolePlay root skill
    )
