/// リポジトリの生成物の一覧と、その一括更新。
/// 一覧は外部の設定ファイルではなくここにF#の値として書く。
/// コンパイラが型を検査し、コメントも書け、パーサも要らない。
/// dotnet runは元々コンパイルするので、足す手間も変わらない。
/// 新しい生徒は個別コマンドで生成してから、以後の一括更新に含めるためここへ足す。
module BluePrompt.Manifest

open System.IO
open System.Threading
open System.Threading.Tasks

/// 一括更新で失敗した対象の表示名と原因。
/// 1件の失敗で他の対象を巻き添えにせず、全ての失敗をまとめて報告する。
exception GenerationFailed of failures: (string * exn) list

/// キャラ呼称表の生成物。role-playスキルの生成も読むため、パスを共有する。
let private appellationJson =
    "plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json"

/// wikiruから取り込む対象。パスはリポジトリのルートからの相対。
let wikiruTargets: Target.WikiruTarget list =
    [ Target.Appellation(
          "キャラ呼称表",
          "plugins/jp-wikiru-bluearchive/skills/character-appellation/reference.md",
          appellationJson
      )
      Target.School(
          "学校別",
          "plugins/jp-wikiru-bluearchive/skills/character-index-by-group/reference.md"
      )
      Target.StudentSkill("ユウカ", "plugins/jp-wikiru-bluearchive/skills/character-yuuka/SKILL.md")
      Target.StudentSkill(
          "ユウカ（体操服）",
          "plugins/jp-wikiru-bluearchive/skills/character-yuuka-track/SKILL.md"
      )
      Target.StudentSkill(
          "ユウカ（パジャマ）",
          "plugins/jp-wikiru-bluearchive/skills/character-yuuka-pajama/SKILL.md"
      )
      Target.StudentSkill("セイア", "plugins/jp-wikiru-bluearchive/skills/character-seia/SKILL.md")
      Target.StudentSkill(
          "セイア（水着）",
          "plugins/jp-wikiru-bluearchive/skills/character-seia-swimsuit/SKILL.md"
      )
      Target.StudentSkill("コトリ", "plugins/jp-wikiru-bluearchive/skills/character-kotori/SKILL.md")
      Target.StudentSkill(
          "コトリ（応援団）",
          "plugins/jp-wikiru-bluearchive/skills/character-kotori-cheer-squad/SKILL.md"
      )
      Target.RolePlayReference("ユウカ", "plugins/role-play/skills/yuuka/normal.md")
      Target.RolePlayReference("ユウカ（体操服）", "plugins/role-play/skills/yuuka/track.md")
      Target.RolePlayReference("ユウカ（パジャマ）", "plugins/role-play/skills/yuuka/pajama.md")
      Target.RolePlayReference("セイア", "plugins/role-play/skills/seia/normal.md")
      Target.RolePlayReference("セイア（水着）", "plugins/role-play/skills/seia/swimsuit.md")
      Target.RolePlayReference("コトリ", "plugins/role-play/skills/kotori/normal.md")
      Target.RolePlayReference("コトリ（応援団）", "plugins/role-play/skills/kotori/cheer-squad.md") ]

/// テンプレートから本文を生成するrole-playスキル。パスはリポジトリのルートからの相対。
/// 生徒に固有の手書きの部分を書いたcharacter.mdを持つスキルだけを含める。
let rolePlaySkills: Target.RolePlaySkill list =
    [ { Caller = "ユウカ"
        Template = "plugins/role-play"
        Appellation = appellationJson
        Output = "plugins/role-play/skills/yuuka" }
      { Caller = "セイア"
        Template = "plugins/role-play"
        Appellation = appellationJson
        Output = "plugins/role-play/skills/seia" }
      { Caller = "コトリ"
        Template = "plugins/role-play"
        Appellation = appellationJson
        Output = "plugins/role-play/skills/kotori" } ]

/// 同時に進める対象の数。
/// wikiru側の負荷は1台のPCから送る量では誤差だが、
/// 無駄に並べても利益が無く、1対象ごとにpandocのプロセスも立つため、常識的な数に収める。
let degreeOfParallelism: int = 16

/// 名前付きの処理を同時実行数を絞って並列に走らせ、結果を名前ごとのResultで返す。
/// 1件の失敗で他を打ち切らず全件を走らせ切り、
/// 失敗をまとめて報告できるようにParallel.ForEachAsyncではなくこの形にする。
/// 結果の並びは入力の並びと同じ。
let runBounded
    (degree: int)
    (works: (string * (unit -> Task<'T>)) list)
    : Task<(string * Result<'T, exn>) list> =
    task {
        use gate = new SemaphoreSlim(degree)

        let! results =
            works
            |> List.map (fun (name, work) ->
                task {
                    do! gate.WaitAsync()

                    try
                        try
                            let! result = work ()
                            return name, Ok result
                        with error ->
                            return name, Error error
                    finally
                        gate.Release() |> ignore
                })
            |> Task.WhenAll

        return List.ofArray results
    }

/// 成功した結果のパスを集め、失敗があればそれらを束ねて返す。
let private partition
    (results: (string * Result<string list, exn>) list)
    : string list * (string * exn) list =
    let paths =
        results
        |> List.collect (fun (_, result) ->
            match result with
            | Ok paths -> paths
            | Error _ -> [])

    let failures =
        results
        |> List.choose (fun (name, result) ->
            match result with
            | Ok _ -> None
            | Error error -> Some(name, error))

    paths, failures

/// character.mdが挙げるナレッジのうち実在しなかった名前を、character.mdのパスごとに並べたもの。
exception KnowledgeSkillMissing of failures: (string * string list) list

/// 生徒個別スキルの名前。スキル名は出力先のディレクトリの名前と一致する。
let studentSkillNames (targets: Target.WikiruTarget list) : Set<string> =
    targets
    |> List.choose (function
        | Target.StudentSkill(_, output) -> Path.GetDirectoryName output |> Path.GetFileName |> Some
        | _ -> None)
    |> Set.ofList

/// character.mdが挙げるナレッジのうち、生徒個別スキルとして実在しない名前を集める。
/// この名前は生成物へ参照先としてそのまま書き出されるため、
/// 綴りを間違えても生成対象を足し忘れても、生成物の差分だけでは気付けない。
/// 実在するものだけになっていれば空を返す。
let missingKnowledgeSkills
    (known: Set<string>)
    (declared: (string * string list) list)
    : (string * string list) list =
    declared
    |> List.choose (fun (path, knowledge) ->
        match
            RolePlay.studentKnowledgeNames knowledge
            |> List.filter (fun name -> not (Set.contains name known))
        with
        | [] -> None
        | missing -> Some(path, missing))

/// role-playスキルのcharacter.mdが挙げるナレッジが、全て実在する生徒個別スキルかを確かめる。
/// 実在しない名前があればKnowledgeSkillMissingを送出する。
/// wikiruTargetsとrolePlaySkillsの両方を知っているのはここだけなので、この検査もここが持つ。
let private checkKnowledgeSkills (root: string) : Task<unit> =
    task {
        let declared = ResizeArray()

        for skill in rolePlaySkills do
            let path = Path.Combine(root, skill.Output, SkillFile.character)

            let! content = File.ReadAllTextAsync path
            declared.Add(path, (OpenWebui.parseFrontmatter path content).Knowledge)

        match missingKnowledgeSkills (studentSkillNames wikiruTargets) (List.ofSeq declared) with
        | [] -> ()
        | failures -> return raise (KnowledgeSkillMissing failures)
    }

/// role-playスキルを全て並列に書き出し、書いたパスを返す。整形は掛けない。
/// 失敗があればGenerationFailedを送出する。
let writeRolePlaySkills (root: string) : Task<string list> =
    task {
        do! checkKnowledgeSkills root

        let! results =
            rolePlaySkills
            |> List.map (fun skill ->
                Target.rolePlayName skill,
                (fun () -> Target.writeRolePlay (Target.resolveRolePlay root skill)))
            |> runBounded degreeOfParallelism

        match partition results with
        | paths, [] -> return paths
        | _, failures -> return raise (GenerationFailed failures)
    }

/// role-playスキルを全て生成し直してから、まとめてnix fmtを掛ける。
/// wikiruへはアクセスしないため、テンプレートの変更の反映と生成物の検査に使う。
let createRolePlaySkills (root: string) : Task<unit> =
    task {
        let! paths = writeRolePlaySkills root
        do! Fmt.formatFiles paths
    }

/// wikiruの取得の結果を受けて、role-playスキルの生成と整形と失敗の報告を決める。
/// 全て成功していればrole-playスキルを書き出し、
/// wikiruとrole-playのパスをまとめてformatFilesの1回で整形する。
/// 失敗があれば成功した分だけを整形して途中まで更新された状態を整えた上で、
/// GenerationFailedを送出する。
/// その整形が落ちた場合も取得の失敗の理由は失わず、整形の失敗を一覧へ足して送出する。
/// role-playスキルは呼称表と衣装別の参照ファイルを読むため、
/// それらが古いままかもしれない失敗時には生成し直さない。
/// 整形とrole-playの書き出しは引数で受け取り、
/// wikiruへ取りに行かずにこの分岐を検証できるようにする。
let finish
    (formatFiles: string list -> Task<unit>)
    (writeRolePlay: unit -> Task<string list>)
    (results: (string * Result<string list, exn>) list)
    : Task<unit> =
    task {
        match partition results with
        | wikiruPaths, [] ->
            let! rolePlayPaths = writeRolePlay ()
            do! formatFiles (wikiruPaths @ rolePlayPaths)
        | wikiruPaths, failures ->
            // 整形が落ちても取得の失敗の理由は報告したいので、
            // 整形の失敗は失敗の一覧の末尾へ加えてまとめて送出する。
            let! formatFailure =
                task {
                    try
                        do! formatFiles wikiruPaths
                        return []
                    with error ->
                        return [ "nix fmt", error ]
                }

            return raise (GenerationFailed(failures @ formatFailure))
    }

/// wikiruの対象を全て並列に取得して書き出し、
/// 続けてrole-playスキルを生成し直してから、まとめてnix fmtを1回だけ掛ける。
/// 失敗時の扱いはfinishのとおり。
let createAll (root: string) : Task<unit> =
    task {
        let! results =
            wikiruTargets
            |> List.map (fun target ->
                Target.wikiruName target,
                (fun () -> Target.writeWikiru (Target.resolveWikiru root target)))
            |> runBounded degreeOfParallelism

        do! finish Fmt.formatFiles (fun () -> writeRolePlaySkills root) results
    }
