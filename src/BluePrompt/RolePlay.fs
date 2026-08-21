/// role-playスキルの本文の組み立て。
/// 届け先はClaude Code向けのSKILL.mdとOpen WebUIのModel向けのMODEL.mdの2つで、
/// どちらを組み立てるかは渡されたテンプレートが決める。
/// 本文の骨格は全生徒で共通のテンプレート1つが持ち、
/// その決まった位置へ、
/// 生徒に固有の手書きの部分と、
/// 生徒個別ページから作った衣装別の参照ファイルと、
/// キャラ呼称表のJSONから読んだ事実を差し込む。
/// wikiruへはアクセスせず、リポジトリへ併置した生成物だけで完結する。
/// character.mdのフロントマターの読み取りはOpenWebuiのものを共有するため、
/// コンパイルの順序はそちらより後になる。
module BluePrompt.RolePlay

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks

/// 通常衣装の参照ファイルの名前。
/// 衣装の一覧では基本になる姿から並べたいので、これを先頭へ置く。
let baseFileName: string = "normal.md"

/// スキルのディレクトリにあるMarkdownのうち、衣装別の参照ファイルではないもの。
let private nonReferenceFileNames =
    Set.ofList [ SkillFile.skill; SkillFile.model; SkillFile.character ]

/// 参照ファイル1つ分の、本文から参照するために要る情報。
/// 中身はスキル本体へ写さず、ファイルとして読ませるので、ここには持たない。
type Reference =
    {
        /// スキルのディレクトリから見たファイル名。本文からのリンクに使う。
        FileName: string
        /// 出典のwikiruのページ名。衣装の区別はこの名前が担う。
        PageName: string
    }

/// 参照ファイルから読み取れなかった内容と、そのファイルのパス。
exception ReferenceShapeError of path: string * missing: string

/// 衣装別の参照ファイルが1つも無かった時のスキルのディレクトリ。
exception ReferenceNotFound of directory: string

/// 参照ファイル先頭の出典の行。
let private sourcePattern =
    Regex(@"^出典: \[[^\]]*\]\((?<url>[^)]+)\)", RegexOptions.Compiled)

/// 参照ファイルの内容から、本文で参照するために要る情報を読み取る。
/// 読めなかった項目はReferenceShapeErrorで報せる。
/// 欠けたまま組み立てると、一覧からその衣装が黙って消えるだけになるため。
let parseReference (path: string) (markdown: string) : Reference =
    let missing name =
        raise (ReferenceShapeError(path = path, missing = name))

    // 壊れたリンクをUriのコンストラクタへ渡すとUriFormatExceptionが飛び、
    // どのファイルが壊れているのかがメッセージから消えるため、
    // 絶対URLかどうかはここで判定して他の失敗と同じ形へ寄せる。
    let sourceUri (url: string) : Uri =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri -> uri
        | _ -> missing "出典のURL"

    let pageName =
        match sourcePattern.Match markdown with
        | source when source.Success ->
            match (sourceUri source.Groups["url"].Value).Query.TrimStart '?' with
            | "" -> missing "出典のページ名"
            | query -> Uri.UnescapeDataString query
        | _ -> missing "出典の行"

    { FileName = Path.GetFileName path
      PageName = pageName }

/// 衣装ごとの参照ファイルの一覧のMarkdownを組み立てる。
/// どの衣装のファイルなのかは出典のページ名がそのまま表す。
let toCostumeMarkdown (references: Reference list) : string =
    references
    |> List.map (fun reference ->
        $"- [%s{reference.FileName}](./%s{reference.FileName}): %s{reference.PageName}")
    |> String.concat "\n"

/// スキルのディレクトリから衣装別の参照ファイルを読む。
/// SKILL.mdとMODEL.mdとcharacter.md以外のMarkdownを参照ファイルと見なす。
/// 除外を並べる形なので、
/// スキルのディレクトリへREADME.mdのような別のMarkdownを置くと、
/// 出典の行を持たない衣装の参照ファイルと見なされてReferenceShapeErrorになる。
/// 通常衣装を先頭に置き、残りはファイル名の順に並べる。
/// 1つも無いディレクトリは、衣装の一覧が空のまま書き出されないように失敗にする。
let readReferences (directory: string) : Task<Reference list> =
    task {
        let paths =
            Directory.GetFiles(directory, "*.md")
            |> Array.filter (fun path ->
                not (Set.contains (Path.GetFileName path) nonReferenceFileNames))
            |> Array.sortBy (fun path ->
                let fileName = Path.GetFileName path
                fileName <> baseFileName, fileName)
            |> Array.toList

        if List.isEmpty paths then
            raise (ReferenceNotFound directory)

        let references = ResizeArray()

        for path in paths do
            let! markdown = File.ReadAllTextAsync path
            references.Add(parseReference path markdown)

        return List.ofSeq references
    }

/// テンプレートで演じ方の共通の指示を差し込む位置を示すプレースホルダの名前。
let playingPlaceholder: string = "playing"

/// 全生徒に共通する演じ方の指示。
/// テンプレートへ手で書くと、生徒が増えるたびに同じ文が写されて少しずつずれるため、
/// ここを唯一の出どころにする。
///
/// 口調を要約して書き下すことはしない。
/// 要約は書き手が目を引かれた特徴だけを強めてしまい、
/// 参照ファイルのボイスが持っている語彙と言い回しの幅を潰すため。
/// 一人称と先生の呼び方は呼称の表が持っているので、ここでも繰り返さない。
let playingRules: string =
    String.concat
        "\n"
        [ "- 会話相手のプレイヤーは先生です"
          "- 口調は参照ファイルのボイスの書き起こしから読み取ってください。"
          "  目立つ言い回しだけを繰り返さず、場面ごとの語彙と言葉遣いの幅をそのまま真似てください" ]

/// テンプレートで呼称表を差し込む位置を示すプレースホルダの名前。
let appellationPlaceholder: string = "appellation"

/// テンプレートで衣装ごとの参照ファイルの一覧を差し込む位置を示すプレースホルダの名前。
let costumesPlaceholder: string = "costumes"

/// テンプレートで演じる生徒の呼び名を差し込む位置を示すプレースホルダの名前。
let callerPlaceholder: string = "caller"

/// テンプレートで生徒に固有の手書きの部分を差し込む位置を示すプレースホルダの名前。
let characterPlaceholder: string = "character"

/// テンプレートで生徒のナレッジのスキル名の並びを差し込む位置を示すプレースホルダの名前。
let knowledgeSkillsPlaceholder: string = "knowledgeSkills"

/// 全てのrole-playスキルが参照する、生徒個別ではないナレッジのスキル。
/// 本文では役割が違うので別の文で扱っており、生徒のナレッジの一覧からは除く。
let private sharedKnowledgeNames = Set.ofList [ "character-appellation" ]

/// フロントマターに生徒のナレッジのスキルが1つも無かった時のcharacter.mdのパス。
exception KnowledgeSkillNotFound of path: string

/// character.mdのフロントマターが挙げるナレッジから、
/// この生徒のナレッジのスキル名の一覧のMarkdownを組み立てる。
/// Open WebUIのKnowledgeとの紐付けに要るフロントマターと、
/// 事実の引き方を説明する本文へ同じ一覧を二度書くと、
/// 衣装が増えた時に片方だけ直して食い違う。
/// 1つも無ければ、参照先を挙げない壊れた文を書き出さずに失敗する。
let knowledgeSkillsMarkdown (path: string) (knowledge: string list) : string =
    let names =
        knowledge
        |> List.filter (fun name -> not (Set.contains name sharedKnowledgeNames))

    if List.isEmpty names then
        raise (KnowledgeSkillNotFound path)

    names |> List.map (fun name -> $"- %s{name}") |> String.concat "\n"

/// 全生徒で共通のテンプレートと、併置された生成物から、role-playスキルの本文全体を生成する。
/// SKILL.mdとMODEL.mdのどちらを書き出すかは、渡されたテンプレートと出力先が決める。
/// 没入感を左右する呼称は別ファイルへ分けず、スキル本体へ直接埋め込む。
/// 生徒に固有の手書きの部分と衣装別の参照ファイルは、出力先と同じディレクトリから読む。
/// テンプレートに差し込むのは全生徒に共通する指示か、生徒ごとに決まる事実か、
/// その生徒をどう位置付けるかを書いた手書きの部分だけなので、
/// 本文の骨格は生徒が増えても1つのテンプレートのまま保たれる。
/// 書き出した直後にnix fmtを掛けて、生成コマンドだけで内容が確定するようにする。
let writeSkill
    (caller: string)
    (templatePath: string)
    (jsonPath: string)
    (outputPath: string)
    : Task<unit> =
    task {
        let directory =
            match Path.GetDirectoryName outputPath with
            | null
            | "" -> "."
            | directory -> directory

        let characterPath = Path.Combine(directory, SkillFile.character)
        let! character = File.ReadAllTextAsync characterPath
        let frontmatter = OpenWebui.parseFrontmatter characterPath character
        let! template = File.ReadAllTextAsync templatePath
        let! references = readReferences directory
        let! json = File.ReadAllTextAsync jsonPath
        let document = Appellation.ofJson json

        let appellation =
            Wikiru.sourceHeader (Uri document.Source)
            + Appellation.toCallerMarkdown caller document.Entries

        let values =
            Map
                [ callerPlaceholder, caller
                  characterPlaceholder, frontmatter.Body
                  playingPlaceholder, playingRules
                  appellationPlaceholder, appellation
                  costumesPlaceholder, toCostumeMarkdown references
                  knowledgeSkillsPlaceholder,
                  knowledgeSkillsMarkdown characterPath frontmatter.Knowledge ]

        let skill =
            frontmatter.Raw + "\n\n" + Template.renderOrFail templatePath values template

        do! File.WriteAllTextAsync(outputPath, skill)
        do! Fmt.formatFile outputPath
    }
