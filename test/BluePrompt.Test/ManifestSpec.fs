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

        let work () =
            task {
                let current = Interlocked.Increment inFlight
                record current

                if current = degree then
                    reachedDegree.TrySetResult() |> ignore

                do! release.Task
                Interlocked.Decrement inFlight |> ignore
                return current
            }

        let running =
            Manifest.runBounded degree (List.init 5 (fun index -> $"work%d{index}", work))

        do! reachedDegree.Task
        release.SetResult()
        let! results = running

        Assert.Equal(degree, maxInFlight.Value)
        Assert.Equal<string list>(List.init 5 (fun index -> $"work%d{index}"), List.map fst results)
    }

[<Fact>]
let ``runBoundedは1件の失敗で他を打ち切らず全件の結果を名前付きで返す`` () =
    task {
        let works =
            [ "ok1", (fun () -> Task.FromResult 1)
              "failed", (fun () -> Task.FromException<int>(InvalidOperationException "失敗"))
              "ok2", (fun () -> Task.FromResult 2) ]

        let! results = Manifest.runBounded 16 works

        match results with
        | [ "ok1", Ok 1; "failed", Error(:? InvalidOperationException as error); "ok2", Ok 2 ] ->
            Assert.Equal("失敗", error.Message)
        | other -> failwith $"想定外の結果: %A{other}"
    }

/// マニフェストのwikiru対象が書き出すパスの一覧。
let private wikiruOutputs =
    Manifest.wikiruTargets
    |> List.collect (fun target ->
        match target with
        | Target.Appellation(_, markdownOutput, jsonOutput) -> [ markdownOutput; jsonOutput ]
        | Target.Knowledge(_, output)
        | Target.School(_, output)
        | Target.StudentSkill(_, output)
        | Target.RolePlayReference(_, output) -> [ output ])

[<Fact>]
let ``マニフェストの出力先は重複しない`` () =
    Assert.Equal<string list>(List.distinct wikiruOutputs, wikiruOutputs)

[<Fact>]
let ``生徒スキルの出力先はSKILL.md`` () =
    for target in Manifest.wikiruTargets do
        match target with
        | Target.StudentSkill(_, output) -> Assert.Equal(SkillFile.skill, Path.GetFileName output)
        | _ -> ()

[<Fact>]
let ``role-playスキルが読む呼称表はwikiruの対象が書き出す`` () =
    for skill in Manifest.rolePlaySkills do
        Assert.Contains(skill.Appellation, wikiruOutputs)

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
          Output = "skills/yuuka" }

    Assert.Equal(
        { skill with
            Template = Path.Combine(root, "template")
            Appellation = Path.Combine(root, "appellation.json")
            Output = Path.Combine(root, "skills/yuuka") },
        Target.resolveRolePlay root skill
    )
