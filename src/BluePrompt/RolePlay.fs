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
let private baseFileName: string = "normal.md"

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

/// 代表的な発言が見出しから始まっていなかった時のファイルのパス。
exception QuoteShapeError of path: string

/// 代表的な発言1つ分の見出し。
/// 節へ並べて差し込むので、見出しが無いと発言と発言の切れ目が消える。
let private quoteHeadingPrefix: string = "## "

/// スキルのディレクトリから代表的な発言を読む。
/// 発言はwikiruから作れないため手書きで、
/// 用意していない生徒も居るので、ディレクトリが無ければ0件として扱う。
/// 並びはファイル名の順で、書き手が名前で決められるようにする。
let readQuotes (directory: string) : Task<string list> =
    task {
        let quoteDirectory = Path.Combine(directory, SkillFile.quote)

        if not (Directory.Exists quoteDirectory) then
            return []
        else
            let paths = Directory.GetFiles(quoteDirectory, "*.md") |> Array.sort
            let quotes = ResizeArray()

            for path in paths do
                let! markdown = File.ReadAllTextAsync path
                let quote = markdown.Trim()

                if not (quote.StartsWith(quoteHeadingPrefix, StringComparison.Ordinal)) then
                    raise (QuoteShapeError path)

                quotes.Add quote

            return List.ofSeq quotes
    }

/// テンプレートで代表的な発言を差し込む位置を示すプレースホルダの名前。
let private quotesPlaceholder: string = "quotes"

/// 代表的な発言の節を組み立てる。
/// 他の穴と違いここだけは見出しごと差し込む。
/// 発言を用意していない生徒では見出しだけが残るのを避けたいためで、
/// 0件なら節ごと消える。
let quotesMarkdown (caller: string) (quotes: string list) : string =
    match quotes with
    | [] -> ""
    | quotes ->
        let guide =
            String.concat
                "\n"
                [ $"作中で%s{caller}が実際に話した発言です。"
                  "ボイスの短いセリフだけでは掴めない、話の運び方と長い語りの組み立てが分かるものを選んでいます。"
                  "そのまま繰り返さず、言葉選びと話の展開の手本として使ってください。" ]

        String.concat "\n\n" ([ "# 代表的な発言"; guide ] @ quotes)

/// テンプレートで演じ方の共通の指示を差し込む位置を示すプレースホルダの名前。
let private playingPlaceholder: string = "playing"

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
let private appellationPlaceholder: string = "appellation"

/// テンプレートで衣装ごとの参照ファイルの一覧を差し込む位置を示すプレースホルダの名前。
let private costumesPlaceholder: string = "costumes"

/// テンプレートで演じる生徒の呼び名を差し込む位置を示すプレースホルダの名前。
let private callerPlaceholder: string = "caller"

/// テンプレートで生徒に固有の手書きの部分を差し込む位置を示すプレースホルダの名前。
let private characterPlaceholder: string = "character"

/// テンプレートで生徒のナレッジのスキル名の並びを差し込む位置を示すプレースホルダの名前。
let private knowledgeSkillsPlaceholder: string = "knowledgeSkills"

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

/// 本文を組み立てるのに要る、ファイルから読み終えた入力一式。
/// 組み立てをファイル入出力から切り離して、
/// 値とプレースホルダの対応をテストで固定できるようにするために持つ。
type SkillInput =
    {
        /// 演じる生徒の呼び名。呼称表の引き当てにも使う。
        Caller: string
        /// 全生徒で共通のテンプレートのパス。差し込みの食い違いの報告に使う。
        TemplatePath: string
        /// 全生徒で共通のテンプレートの中身。
        Template: string
        /// character.mdのパス。ナレッジが1つも無かった時の報告に使う。
        CharacterPath: string
        /// character.mdのフロントマターと、生徒に固有の手書きの本文。
        Character: OpenWebui.Frontmatter
        /// 衣装別の参照ファイル。
        References: Reference list
        /// 代表的な発言。用意していない生徒では空になる。
        Quotes: string list
        /// キャラ呼称表。
        Appellation: Appellation.Document
    }

/// character.mdが本文を持たなかった時のcharacter.mdのパス。
exception CharacterBodyNotFound of path: string

/// 読み終えた入力からrole-playスキルの本文の文字列を組み立てる。
/// フロントマターは解釈せず、テンプレートを差し込んだ本文の前へそのまま置く。
/// 没入感を左右する呼称は別ファイルへ分けず、本文へ直接埋め込む。
/// 差し込むのは全生徒に共通する指示か、生徒ごとに決まる事実か、
/// その生徒をどう位置付けるかを書いた手書きの部分だけなので、
/// 本文の骨格は生徒が増えても1つのテンプレートのまま保たれる。
let renderSkill (input: SkillInput) : string =
    // 参照ファイルが0件なら、ナレッジが0件ならと同じく、
    // 差し込むものが無いまま本文を書き出さない。
    // その生徒をどう位置付けるかは手で書く唯一の場所なので、
    // 移行の時に書き忘れると黙って落ちる。
    if String.IsNullOrEmpty input.Character.Body then
        raise (CharacterBodyNotFound input.CharacterPath)

    let appellation =
        Wikiru.sourceHeader (Uri input.Appellation.Source)
        + Appellation.toCallerMarkdown input.Caller input.Appellation.Entries

    let values =
        Map
            [ callerPlaceholder, input.Caller
              characterPlaceholder, input.Character.Body
              playingPlaceholder, playingRules
              appellationPlaceholder, appellation
              costumesPlaceholder, toCostumeMarkdown input.References
              quotesPlaceholder, quotesMarkdown input.Caller input.Quotes
              knowledgeSkillsPlaceholder,
              knowledgeSkillsMarkdown input.CharacterPath input.Character.Knowledge ]

    input.Character.Raw
    + "\n\n"
    + Template.renderOrFail input.TemplatePath values input.Template

/// 届け先の、骨格になるテンプレートと書き出す本文のファイル名の組。
/// どちらの届け先も同じ入力から組み立てるので、生成は常に両方まとめて行う。
let private destinations =
    [ SkillFile.skillTemplate, SkillFile.skill
      SkillFile.modelTemplate, SkillFile.model ]

/// 全生徒で共通のテンプレートと、併置された生成物から、role-playスキルの本文を書き出す。
/// 届け先はClaude Code向けのSKILL.mdとOpen WebUIのModel向けのMODEL.mdの2つで、
/// テンプレートも出力もファイル名が決まっているため、受け取るのはそれぞれのディレクトリになる。
/// 生徒に固有の手書きの部分と衣装別の参照ファイルは、出力先のディレクトリから読む。
/// 整形は掛けず、書き出した2つのパスを返す。
/// 整形の呼び出しは一括生成で1回にまとめられるように書き出しと分けてTargetが持つ。
let writeSkill
    (caller: string)
    (templateDirectory: string)
    (jsonPath: string)
    (outputDirectory: string)
    : Task<string list> =
    task {
        let characterPath = Path.Combine(outputDirectory, SkillFile.character)
        let! character = File.ReadAllTextAsync characterPath
        let! references = readReferences outputDirectory
        let! quotes = readQuotes outputDirectory
        let! json = File.ReadAllTextAsync jsonPath
        let frontmatter = OpenWebui.parseFrontmatter characterPath character
        let document = Appellation.ofJson json

        let paths =
            destinations
            |> List.map (fun (templateName, outputName) ->
                Path.Combine(templateDirectory, templateName),
                Path.Combine(outputDirectory, outputName))

        for templatePath, outputPath in paths do
            let! template = File.ReadAllTextAsync templatePath

            let skill =
                renderSkill
                    { Caller = caller
                      TemplatePath = templatePath
                      Template = template
                      CharacterPath = characterPath
                      Character = frontmatter
                      References = references
                      Quotes = quotes
                      Appellation = document }

            do! File.WriteAllTextAsync(outputPath, skill)

        return List.map snd paths
    }
