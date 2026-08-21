/// スキルをOpen WebUIのKnowledgeコレクションの定義へ変換する。
///
/// 参照して事実を引くだけのスキルは、
/// Modelのシステムプロンプトへ焼き込んでも、
/// 会話の入口としてそのModelが選ばれない限り読まれないため意味がない。
/// Open WebUIでこの種のスキルに対応するのはKnowledgeで、
/// ロールプレイ用のModelから参照させたり、
/// チャットで`#`を打って明示的に引いたりできる。
///
/// Knowledgeはファイル単位で登録してチャンクへ割られるため、
/// SKILL.mdと参照ファイルを見出しの単位へ分割したファイル群として書き出す。
module BluePrompt.OpenWebuiKnowledge

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Encodings.Web
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Open WebUIのKnowledgeコレクションの作成フォーム。
/// backend/open_webui/models/knowledge.pyのKnowledgeFormに対応する。
/// access_grantsは登録先の利用者構成に依存するため生成物では持たず、
/// 作成したインスタンスの既定に任せる。
type KnowledgeForm = { Name: string; Description: string }

/// 1つの断片を書き出したファイル。
type KnowledgeFile = { FileName: string; Content: string }

/// コレクション1つ分の定義。
type Knowledge =
    { Form: KnowledgeForm
      Files: KnowledgeFile list }

/// 分割後の1ファイルの目安の大きさ。
///
/// Open WebUIの既定のチャンクは1000文字程度なので、
/// この大きさなら1ファイルが数チャンクに収まり、
/// 検索で当たったチャンクの周辺だけを読んでも節として意味を保てる。
/// 呼称表であれば学校や部活の単位に落ち着く。
let maxFragmentBytes = 8192

/// ファイル名に使えない文字と、階層の区切りに使うため名前の中では避けたい文字。
let private unsafeFileNamePattern = Regex @"[\\/:*?""<>|#\s]+"

/// 見出しの並びからファイル名の本体を組み立てる。
/// Open WebUIのUIにはファイル名がそのまま並ぶため、
/// どの節なのかが読んで分かる名前にする。
///
/// 先頭のstemはフロントマターの名前と参照ファイル名に由来し、
/// 見出しと同じく区切り文字を含み得るため、同じサニタイズを通す。
let private toFileNameBase (stem: string) (headings: string list) : string =
    let sanitize (text: string) =
        unsafeFileNamePattern.Replace(text, "-").Trim '-'

    stem :: headings
    |> List.choose (fun part ->
        match sanitize part with
        | "" -> None
        | sanitized -> Some sanitized)
    |> String.concat "-"

/// ファイル名の本体に許す最大のバイト数。
/// 多くのファイルシステムの上限が255バイトなので、
/// この後に足す拡張子と重複回避の連番の分を余裕を持って残した値にする。
let private maxFileNameBytes = 200

/// 上限を超える名前を、UTF-8の文字の途中で切らずに詰める。
///
/// UTF-16のコード単位を1つずつ削るとサロゲートペアの途中で切れて、
/// 単独のサロゲートを含む壊れた名前になる。
/// 文字の単位で先頭から積み上げて、上限を超える手前でやめる。
let private truncate (name: string) : string =
    let mutable index = 0
    let mutable bytes = 0
    let mutable finished = false

    while not finished && index < name.Length do
        // サロゲートペアは2つのコード単位で1文字なので、まとめて数える。
        let length =
            if Char.IsHighSurrogate name[index] && index + 1 < name.Length then
                2
            else
                1

        let next = bytes + Text.Encoding.UTF8.GetByteCount(name.AsSpan(index, length))

        if maxFileNameBytes < next then
            finished <- true
        else
            bytes <- next
            index <- index + length

    name.Substring(0, index)

/// 断片へ重複しないファイル名を与える。
/// 同じ見出しが別の場所に現れると名前が衝突するため、
/// 2つ目以降にだけ連番を足して先着の名前を安定させる。
let private assignFileNames
    (skillPath: string)
    (stem: string)
    (fragments: Markdown.Fragment list)
    : KnowledgeFile list =
    let used = Collections.Generic.HashSet<string>()

    fragments
    |> List.map (fun fragment ->
        let baseName = truncate (toFileNameBase stem fragment.Headings)

        let rec uniqueName (candidate: string) (suffix: int) =
            if used.Add candidate then
                candidate
            else
                uniqueName $"%s{baseName}-%d{suffix + 1}" (suffix + 1)

        let fileName = $"%s{uniqueName baseName 1}.md"

        // 名前はPath.Combineで書き出しに使われ、
        // アップロードのmultipartのfilenameとしても送られる。
        // サニタイズを抜けた区切り文字が残っていないことをここで確かめて、
        // 生成物のディレクトリの外を指す名前を作らないようにする。
        if String.IsNullOrEmpty baseName || fileName <> Path.GetFileName fileName then
            raise (OpenWebui.SkillFormatError(skillPath, $"断片のファイル名を組み立てられません: %s{fileName}"))

        { FileName = fileName
          Content = fragment.Text })

/// wikiruから生成した文書が持つ出典の行。
/// `sourceHeader`が組み立てる「出典: [記事名](URL)」の形をそのまま拾う。
let private sourcePattern =
    Regex(@"^出典: \[[^\]]+\]\([^)]+\)$", RegexOptions.Multiline)

/// 本文から出典の行を抜き出し、その行を落とした本文と組にして返す。
/// 見つからない手書きの文書では本文をそのまま返す。
let private takeSourceLine (content: string) : string option * string =
    match sourcePattern.Match content with
    | m when m.Success -> Some m.Value, content.Remove(m.Index, m.Length)
    | _ -> None, content

/// Markdownファイル1つを分割して、名前を与えたファイル群にする。
///
/// ファイル名はコレクションの名前から始めます。
/// 検索で当たった断片は、本文とファイル名だけがLLMへ渡り、
/// どのコレクションから来たのかは伝わりません。
/// 衣装違いの生徒のように似た構造のコレクションが並ぶと、
/// 例えば体操服のEXスキルの表を通常衣装のものとして答えてしまいます。
/// 同じ理由で、出典の行は分割の前に本文から抜いて全ての断片へ配ります。
/// 記事名が本文に入ることで「体操服の」のような問い合わせが検索に効くようになり、
/// 断片を単体で読んだ時にどの記事から来たのかも辿れます。
/// 元の位置に残すと出典の行だけの短い断片ができて、
/// 検索結果の枠を実データの断片から奪ってしまいます。
let private splitFile
    (skillPath: string)
    (collectionName: string)
    (fileName: string)
    (content: string)
    : KnowledgeFile list =
    let stem = Path.GetFileNameWithoutExtension fileName
    let sourceLine, body = takeSourceLine content

    Markdown.splitBySize maxFragmentBytes body
    |> List.map (fun fragment ->
        match sourceLine with
        | None -> fragment
        | Some line ->
            { fragment with
                Text = $"%s{line}\n\n%s{fragment.Text}" })
    |> assignFileNames skillPath $"%s{collectionName}-%s{stem}"

/// SKILL.mdの本文からフロントマターを落とす。
/// フロントマターはClaude Codeがスキルを選ぶための情報で、
/// Knowledgeとしては本文だけが要る。
let private stripFrontmatter (content: string) : string =
    let normalized = content.Replace("\r\n", "\n")

    match normalized.Split '\n' |> Array.toList with
    | first :: rest when first = "---" ->
        match List.tryFindIndex (fun line -> line = "---") rest with
        | Some closeIndex -> rest |> List.skip (closeIndex + 1) |> String.concat "\n" |> _.Trim()
        | None -> normalized
    | _ -> normalized

/// スキルディレクトリからKnowledgeコレクションの定義を組み立てる。
///
/// SKILL.mdの本文と、そこからリンクされたMarkdownの参照ファイルを対象にする。
/// Markdown以外の参照ファイルは、
/// appellation.jsonのようにjqやスクリプトで引くためのもので、
/// Open WebUIにはそれを実行する主体がいないため対象にしない。
/// 同じ内容がMarkdown側にもあるため、埋め込みを作っても検索の役に立たない。
///
/// SKILL.mdの本文は、
/// Grepで探すとかjqで抽出するとか、
/// Open WebUIには存在しないツールの使い方を書いていることがある。
/// それでも本文ごとKnowledgeへ載せているのは、
/// スキルが何を扱う文書なのかという説明も同じ本文にあり、
/// 引き方の記述だけを機械的に切り分ける基準が無いためである。
/// 引き方の段落が検索で当たっても、
/// LLMは持っていないツールの話として読み飛ばすだけで、
/// 実データの断片を押し出すほどの量でもない。
/// Claude Code向けの説明とOpen WebUI向けの知識を1つのSKILL.mdで兼ねる以上、
/// ここは割り切っている。
let buildKnowledge (skillDirectory: string) : Knowledge =
    let skillPath = Path.Combine(skillDirectory, "SKILL.md")

    if not (File.Exists skillPath) then
        raise (OpenWebui.SkillFormatError(skillPath, "SKILL.mdが存在しません"))

    let skillContent = File.ReadAllText skillPath
    let frontmatter = OpenWebui.parseFrontmatter skillPath skillContent

    let referenceFiles =
        OpenWebui.resolveReferences skillDirectory skillPath frontmatter.Body
        |> List.choose (fun (fileName, filePath) ->
            match (Path.GetExtension fileName).TrimStart '.' with
            | "md"
            | "markdown" -> Some(fileName, File.ReadAllText filePath)
            | _ ->
                // 対象外にしたことが生成のログから分かるようにする。
                printfn $"%s{skillPath}: %s{fileName}はMarkdownではないためKnowledgeへ含めません"
                None)

    let files =
        splitFile skillPath frontmatter.Name "SKILL.md" (stripFrontmatter skillContent)
        @ List.collect
            (fun (fileName, content) -> splitFile skillPath frontmatter.Name fileName content)
            referenceFiles

    { Form =
        { Name = frontmatter.Name
          Description = frontmatter.Description }
      Files = files }

/// JSON直列化の設定。
/// OpenWebui側と同じく、キーはsnake_caseへ揃えて日本語はそのまま書く。
let private serializerOptions =
    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    JsonFSharpOptions.Default().AddToJsonSerializerOptions options
    options

/// KnowledgeFormをOpen WebUIのAPIへ渡せるJSON文字列へ直列化する。
let toJson (form: KnowledgeForm) : string =
    JsonSerializer.Serialize(form, serializerOptions) + "\n"

/// toJsonの逆変換。
let ofJson (json: string) : KnowledgeForm =
    JsonSerializer.Deserialize<KnowledgeForm>(json, serializerOptions)

/// コレクションの定義を置くディレクトリの中で、フォームのJSONに使う名前。
let formFileName = "knowledge.json"

/// コレクションへ登録するファイルを置くディレクトリの名前。
let filesDirectoryName = "files"

/// スキルディレクトリからKnowledgeコレクションの定義一式を書き出す。
/// 出力はNixのビルド成果物でリポジトリへはコミットしないため、nix fmtは掛けない。
let writeKnowledge (skillDirectory: string) (outputDirectory: string) : Task<unit> =
    task {
        let knowledge = buildKnowledge skillDirectory
        let filesDirectory = Path.Combine(outputDirectory, filesDirectoryName)
        Directory.CreateDirectory filesDirectory |> ignore

        do!
            File.WriteAllTextAsync(
                Path.Combine(outputDirectory, formFileName),
                toJson knowledge.Form
            )

        // 断片はいずれも数KBで互いに独立しているため、
        // 呼称表のように100近くへ分かれる場合でも待ちを積み上げずにまとめて待つ。
        do!
            knowledge.Files
            |> List.map (fun file ->
                File.WriteAllTextAsync(Path.Combine(filesDirectory, file.FileName), file.Content))
            |> Task.WhenAll
    }
