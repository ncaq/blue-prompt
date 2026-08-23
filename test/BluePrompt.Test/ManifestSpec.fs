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

/// 整形の呼び出しを記録するスタブ。呼ばれた回数とパスを後から検査する。
let private recordingFormat (calls: ResizeArray<string list>) (paths: string list) : Task<unit> =
    calls.Add paths
    Task.FromResult()

[<Fact>]
let ``finishは全成功ならrole-playも書き出して1回でまとめて整形する`` () =
    task {
        let calls = ResizeArray()

        do!
            Manifest.finish
                (recordingFormat calls)
                (fun () -> Task.FromResult [ "SKILL.md"; "MODEL.md" ])
                [ "a", Ok [ "a.md" ]; "b", Ok [ "b.md"; "b.json" ] ]

        Assert.Equal<string list list>(
            [ [ "a.md"; "b.md"; "b.json"; "SKILL.md"; "MODEL.md" ] ],
            List.ofSeq calls
        )
    }

[<Fact>]
let ``finishは失敗があれば成功分だけ整形しrole-playは書き出さずGenerationFailedを送出する`` () =
    task {
        let calls = ResizeArray()
        let rolePlayWritten = ref false
        let error: exn = InvalidOperationException "取得に失敗"

        let! raised =
            Assert.ThrowsAsync<Manifest.GenerationFailed>(fun () ->
                Manifest.finish
                    (recordingFormat calls)
                    (fun () ->
                        rolePlayWritten.Value <- true
                        Task.FromResult [])
                    [ "a", Ok [ "a.md" ]; "b", Error error ])

        Assert.Equal<(string * exn) list>([ "b", error ], raised.failures)
        Assert.Equal<string list list>([ [ "a.md" ] ], List.ofSeq calls)
        Assert.False rolePlayWritten.Value
    }

[<Fact>]
let ``finishは失敗の報告で整形が落ちても取得の失敗の理由を失わない`` () =
    task {
        let fetchError = InvalidOperationException "取得に失敗"
        let formatError = InvalidOperationException "整形に失敗"

        let! raised =
            Assert.ThrowsAsync<Manifest.GenerationFailed>(fun () ->
                Manifest.finish
                    (fun _ -> Task.FromException<unit> formatError)
                    (fun () -> Task.FromResult [])
                    [ "a", Ok [ "a.md" ]; "b", Error fetchError ])

        // 取得の失敗が先頭に残り、整形の失敗もその後ろに並ぶ。
        match raised.failures with
        | [ "b", (:? InvalidOperationException as first)
            _, (:? InvalidOperationException as second) ] ->
            Assert.Equal("取得に失敗", first.Message)
            Assert.Equal("整形に失敗", second.Message)
        | other -> failwith $"想定外の失敗の一覧: %A{other}"
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
