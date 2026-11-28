/// role-playスキルの本文の組み立て。
/// 本文の骨格は全生徒で共通のテンプレート1つが持ち、
/// その決まった位置へ、
/// 生徒に固有の手書きの部分と、
/// 生徒個別ページから作った衣装別の参照ファイルと、
/// キャラ呼称表のJSONから読んだ事実と、
/// quote/へ手で置いた代表的な発言を差し込む。
/// wikiruへはアクセスせず、リポジトリへ併置した生成物だけで完結する。
/// character.mdのフロントマターの読み取りはOpenWebuiのものを共有するため、
/// コンパイルの順序はそちらより後になる。
module BluePrompt.RolePlay

open System
open System.IO
open System.Threading.Tasks
open Microsoft.FSharp.Reflection

/// 通常衣装の参照ファイルの名前。
/// 衣装の一覧では基本になる姿から並べたいので、これを先頭へ置く。
let private baseFileName: string = "normal.md"

/// スキルのディレクトリにあるMarkdownのうち、衣装別の参照ファイルではないもの。
let private nonReferenceFileNames =
    Set.ofList [ SkillFile.skill; SkillFile.character ]

/// 参照ファイル1つ分の、本文へ差し込むために要る情報。
/// 中身は本文へそのまま写す。
/// 衣装別のボイスを持たせるのは場面ごとに衣装を再現させるためではなく、
/// 口調を安定させる材料を増やすためなので、
/// 場面に合う衣装を選ばせずに、演じ始める前に全ての衣装へ目を通させる。
/// その前提なので、
/// 別ファイルとして開かせても読ませる量は変わらず、
/// 開くかどうかの判断とファイルを開く往復だけが増えるため。
type Reference =
    {
        /// 出典のwikiruのページ名。衣装の区別はこの名前が担う。
        PageName: string
        /// 参照ファイルの中身。出典の行を含む全体を、前後の空白だけ落として持つ。
        Body: string
    }

/// 参照ファイルから読み取れなかった内容と、そのファイルのパス。
exception ReferenceShapeError of path: string * missing: string

/// 参照ファイルが一段目の見出しを含んでいた時の、そのファイルのパス。
exception ReferenceHeadingError of path: string

/// 衣装別の参照ファイルが1つも無かった時のスキルのディレクトリ。
exception ReferenceNotFound of directory: string

/// 参照ファイルの内容から、本文へ差し込むために要る情報を読み取る。
/// 読めなかった項目はReferenceShapeErrorで報せる。
/// 欠けたまま組み立てると、どの衣装のものなのかを言わない節が本文へ並ぶため。
///
/// 中身が一段目の見出しを持っていないことも確かめる。
/// 衣装の節は一段目の見出しを立てた下へ中身をそのまま収めるので、
/// 中身に一段目の見出しが現れると節の切れ目が消えて、
/// 以降の内容が別の衣装の兄弟として並ぶ。
/// ページの読み方を変えた時にしか起きないが、生成物を目で追わない限り気付けない壊れ方なので、
/// 見出しから始まらない発言をQuoteShapeErrorで弾くのと同じく、読んだ時点で止める。
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
        // 出典の行は参照ファイルの先頭にある。
        match Parse.tryPrefix Wikiru.sourceLine markdown with
        | Some url ->
            match (sourceUri url).Query.TrimStart '?' with
            | Str.NullOrEmpty -> missing "出典のページ名"
            | Str.NonEmpty query -> Uri.UnescapeDataString query
        | None -> missing "出典の行"

    let body = markdown.Trim()

    // 一段目の見出しはどこに現れても文書の直下の節になる。
    if
        (Markdown.parseSections body).Children
        |> List.exists (fun section -> section.Level = 1)
    then
        raise (ReferenceHeadingError path)

    { PageName = pageName; Body = body }

/// 衣装の節の見出しの前置き。
/// 参照ファイルの見出しは基本情報から始まる二段目なので、
/// 一段目のこの見出しを立てて、その下へ中身をそのまま収めると階層が揃う。
let private costumeHeadingPrefix: string = "# 衣装: "

/// 衣装ごとの参照データを節として組み立てる。
/// どの衣装なのかは出典のページ名がそのまま表す。
/// 参照ファイルへのリンクは残さない。
/// 中身が本文にある以上リンクは同じものを指す重複でしかなく、
/// Open WebUIのModelにはそもそも開く手段が無いため。
let toCostumeMarkdown (references: Reference list) : string =
    references
    |> List.map (fun reference ->
        $"%s{costumeHeadingPrefix}%s{reference.PageName}\n\n%s{reference.Body}")
    |> String.concat "\n\n"

/// スキルのディレクトリから衣装別の参照ファイルを読む。
/// SKILL.mdとcharacter.md以外のMarkdownを参照ファイルと見なす。
/// 除外を並べる形なので、
/// スキルのディレクトリへREADME.mdのような別のMarkdownを置くと、
/// 出典の行を持たない衣装の参照ファイルと見なされてReferenceShapeErrorになる。
/// 通常衣装を先頭に置き、残りはファイル名の順に並べる。
/// 1つも無いディレクトリは、衣装の節を1つも持たない本文を書き出さないように失敗にする。
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

/// 全生徒に共通する演じ方の指示。
/// テンプレートへ手で書くと、生徒が増えるたびに同じ文が写されて少しずつずれるため、
/// ここを唯一の出どころにする。
///
/// 口調を要約して書き下すことはしない。
/// 要約は書き手が目を引かれた特徴だけを強めてしまい、
/// 参照データのボイスが持っている語彙と言い回しの幅を潰すため。
/// 一人称と先生の呼び方は呼称の表が持っているので、ここでも繰り返さない。
let playingRules: string =
    String.concat
        "\n"
        [ "- 会話相手のプレイヤーは先生です"
          "- 口調は衣装ごとの参照データのボイスの書き起こしから読み取ってください。"
          "  目立つ言い回しだけを繰り返さず、場面ごとの語彙と言葉遣いの幅をそのまま真似てください" ]

/// 全てのrole-playスキルが参照する、生徒個別ではないナレッジのスキル。
/// 本文では役割が違うので別の文で扱っており、生徒のナレッジの一覧からは除く。
let private sharedKnowledgeNames = Set.ofList [ "character-appellation" ]

/// フロントマターに生徒のナレッジのスキルが1つも無かった時のcharacter.mdのパス。
exception KnowledgeSkillNotFound of path: string

/// フロントマターが挙げるナレッジから、この生徒のナレッジのスキル名だけを残す。
/// 本文の組み立てと、その名前が実在するかの検査の両方が同じ絞り込みを要るため、
/// 除くものの集合を2箇所へ書かずに済むようにここに置く。
let studentKnowledgeNames (knowledge: string list) : string list =
    knowledge
    |> List.filter (fun name -> not (Set.contains name sharedKnowledgeNames))

/// character.mdのフロントマターが挙げるナレッジから、
/// この生徒のナレッジのスキル名の一覧のMarkdownを組み立てる。
/// Open WebUIのKnowledgeとの紐付けに要るフロントマターと、
/// 事実の引き方を説明する本文へ同じ一覧を二度書くと、
/// 衣装が増えた時に片方だけ直して食い違う。
/// 1つも無ければ、参照先を挙げない壊れた文を書き出さずに失敗する。
let knowledgeSkillsMarkdown (path: string) (knowledge: string list) : string =
    let names = studentKnowledgeNames knowledge

    if List.isEmpty names then
        raise (KnowledgeSkillNotFound path)

    names |> List.map (fun name -> $"- %s{name}") |> String.concat "\n"

/// 全生徒で共通のテンプレート(plugins/role-play/SKILL.template.md)の穴へ差し込む値。
/// フィールド名の先頭を小文字にした名前が、テンプレートに書く`{{名前}}`の穴になる。
/// テンプレートはここの穴を全て使う必要があり、
/// 過不足があると差し込むはずの内容が黙って落ちないようにTemplate.renderOrFailが止める。
///
/// フィールド名はテンプレートとの約束なので、名前を変える時はテンプレートの穴も直す。
/// 穴へは文字列をそのまま差し込むため、フィールドは全てstringにする。
type TemplateValues =
    {
        /// 演じる生徒の呼び名。
        Caller: string
        /// character.mdの本文。その生徒をどう位置付けるかを書いた、生徒に固有の手書きの部分。
        Character: string
        /// 全生徒に共通する演じ方の指示。中身はplayingRules。
        Playing: string
        /// キャラ呼称表のJSONから抜き出した、この生徒が誰をどう呼ぶかの表。
        Appellation: string
        /// 衣装別の参照ファイルの中身。出典のページ名の見出しを立てた下へそのまま入る。
        Costumes: string
        /// quote/に置いた代表的な発言。
        /// 他の穴と違い見出しごと差し込まれ、発言を持たない生徒では節ごと消える。
        Quotes: string
        /// character.mdのフロントマターのknowledge:から、
        /// 全生徒が参照するものを除いた、この生徒のナレッジのスキル名の一覧。
        KnowledgeSkills: string
    }

/// テンプレートの穴の名前から差し込む値への対応にする。
/// 穴の名前はTemplateValuesのフィールド名から導いて、名前を書く場所を1つにする。
/// string以外のフィールドを足すとコンパイルは通ってしまうため、
/// 実行時にどのフィールドなのかを添えて止める。
let private templateValueMap (values: TemplateValues) : Map<string, string> =
    FSharpType.GetRecordFields typeof<TemplateValues>
    |> Array.map (fun field ->
        let name = Char.ToLowerInvariant field.Name[0] |> string<char>

        match field.GetValue values with
        | :? string as value -> name + field.Name.Substring 1, value
        | _ -> invalidOp $"TemplateValuesの%s{field.Name}がstringではありません")
    |> Map.ofArray

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
/// 全生徒に効く指示はテンプレートを、その生徒だけの指示はcharacter.mdを直して生成し直す。
///
/// 組み立てた本文はClaude Codeのスキルになり、Open WebUIのModelのシステムプロンプトにもそのままなる。
/// ナレッジの引き方は経路で違うが、本文を分けると生成の経路まで二重になるため、
/// テンプレートにはどちらの経路でも成り立つ言い回しだけを書き、
/// 「スキルが読み込まれた」のように片方でしか成り立たない前提は書かない。
/// テンプレートの中へ書いた注釈は生成物へそのまま出るため、この決まりはここに置く。
let renderSkill (input: SkillInput) : string =
    // 参照ファイルが0件なら、ナレッジが0件ならと同じく、
    // 差し込むものが無いまま本文を書き出さない。
    // その生徒をどう位置付けるかは手で書く唯一の場所なので、
    // 移行の時に書き忘れると黙って落ちる。
    if String.IsNullOrEmpty input.Character.Body then
        raise (CharacterBodyNotFound input.CharacterPath)

    // 呼称表に行が無い生徒でも、テンプレートの「表に無い相手」の指示がそのまま効くので、
    // 表が無いことだけを書いて止めずに生成する。
    let appellation =
        Wikiru.sourceHeader (Uri input.Appellation.Source)
        + (Appellation.toCallerMarkdown input.Caller input.Appellation.Entries
           |> Option.defaultValue $"呼称表にはまだ%s{input.Caller}の行がありません。\n")

    let values =
        { Caller = input.Caller
          Character = input.Character.Body
          Playing = playingRules
          Appellation = appellation
          Costumes = toCostumeMarkdown input.References
          Quotes = quotesMarkdown input.Caller input.Quotes
          KnowledgeSkills = knowledgeSkillsMarkdown input.CharacterPath input.Character.Knowledge }

    input.Character.Raw
    + "\n\n"
    + Template.renderOrFail input.TemplatePath (templateValueMap values) input.Template

/// 全生徒で共通のテンプレートと、スキルのディレクトリに併置された生成物から、SKILL.mdを書き出す。
/// 生徒に固有の手書きの部分と衣装別の参照ファイルと代表的な発言は、スキルのディレクトリから読む。
/// テンプレートも出力もファイル名が決まっているため、受け取るのはそれぞれのディレクトリになる。
/// 整形は掛けず、書き出したパスを返す。
/// 整形の呼び出しは一括生成で1回にまとめられるように書き出しと分けてTargetが持つ。
let writeSkill
    (caller: string)
    (templateDirectory: string)
    (jsonPath: string)
    (skillDirectory: string)
    : Task<string list> =
    task {
        let templatePath = Path.Combine(templateDirectory, SkillFile.skillTemplate)
        let outputPath = Path.Combine(skillDirectory, SkillFile.skill)
        let characterPath = Path.Combine(skillDirectory, SkillFile.character)
        let! character = File.ReadAllTextAsync characterPath
        let! references = readReferences skillDirectory
        let! quotes = readQuotes skillDirectory
        let! json = File.ReadAllTextAsync jsonPath
        let! template = File.ReadAllTextAsync templatePath

        let body =
            renderSkill
                { Caller = caller
                  TemplatePath = templatePath
                  Template = template
                  CharacterPath = characterPath
                  Character = OpenWebui.parseFrontmatter characterPath character
                  References = references
                  Quotes = quotes
                  Appellation = Appellation.ofJson json }

        do! File.WriteAllTextAsync(outputPath, body)
        return [ outputPath ]
    }
