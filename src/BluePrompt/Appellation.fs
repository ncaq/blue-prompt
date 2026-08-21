/// wikiruのキャラ呼称表の構造化。
/// 「誰が誰をどう呼ぶか」をレコードの列へパースし、
/// LLM向けのMarkdownと機械読み出し用のJSONの両方をそこから生成する。
/// パースはHTML文字列だけで完結するため、ネットワークを使わずに単体テストで検証できる。
module BluePrompt.Appellation

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization
open AngleSharp.Dom
open AngleSharp.Html.Parser

/// 呼称1件。呼ぶ側と相手の組に呼称が複数ある場合は、呼称ごとに1レコードになる。
type Entry =
    {
        /// 呼ぶ側の所属する学校。ページのh2見出し。
        School: string
        /// 呼ぶ側の所属する部活・組織。ページのh3見出し。
        /// 連邦生徒会のように学校直下へキャラクターが並ぶ場合はNone。
        Club: string option
        /// 呼ぶ側のキャラクター名。ページのh4見出し。
        /// テーブルのキャラクター列はアイコン画像や略称が入って揺れるため使わない。
        Caller: string
        /// 呼ばれる相手。「自分」の行はCallerの一人称を表す。
        Callee: string
        /// 相手の名前に付いた注釈。相手の正体の補足などが入る。
        CalleeNote: string option
        /// 呼称。
        Name: string
        /// 呼称に付いた注釈。「初対面時」のような呼称を使う条件が入る。
        Note: string option
    }

/// JSONファイル全体。出典URLとレコードの列を持つ。
type Document = { Source: string; Entries: Entry list }

/// 呼称表から1件も呼称を得られなかった時。
/// サイト側の構造変更でパースが全滅した可能性が高く、空のデータによる上書きを防ぐ。
exception EntryNotFound

/// 呼称表の行のセル数が想定の3列でなかった時の、その時点のキャラクター名とセル数。
exception RowShapeError of character: string * cellCount: int

/// 指定した呼ぶ側のキャラクターのレコードが1件も無かった時のキャラクター名。
/// 名前の打ち間違いで空のデータを黙って出力するのを防ぐ。
exception CallerNotFound of caller: string

/// 脚注アンカーのidから注釈本文への対応表を#noteから組み立てる。
/// wikiruの脚注定義は「アンカー、本文のspan、brの繰り返し」の並びなので、
/// アンカーから次のbrまでの兄弟ノードのテキストを本文として集める。
/// 区切りのbrが欠けたマークアップでも次の脚注を巻き込まないように、
/// 次の脚注アンカーでも走査を打ち切る。
/// これにより走査は常に自分の定義の範囲で閉じ、全体でもO(ノード数)で済む。
let private noteMap (document: IDocument) : Map<string, string> =
    document.QuerySelectorAll "#note a.note_super"
    |> Seq.choose (fun anchor ->
        match anchor.Id with
        | null -> None
        | id ->
            let text =
                (anchor :> INode)
                |> Seq.unfold (fun node ->
                    match node.NextSibling with
                    | null -> None
                    | :? IElement as element when element.LocalName = "br" -> None
                    | :? IElement as element when element.ClassList.Contains "note_super" -> None
                    | next -> Some(next.TextContent, next))
                |> String.concat ""
                |> _.Trim()

            if String.IsNullOrEmpty text then None else Some(id, text))
    |> Map.ofSeq

/// 本文中の脚注アンカーから注釈本文を引く。
/// href先の#noteの定義を正とし、定義を辿れない場合はtitle属性で代替する。
let private noteOfReference (notes: Map<string, string>) (reference: IElement) : string option =
    let byDefinition =
        match reference.GetAttribute "href" with
        | null -> None
        | href -> Map.tryFind (href.TrimStart '#') notes

    match byDefinition with
    | Some note -> Some note
    | None ->
        match reference.GetAttribute "title" with
        | null
        | "" -> None
        | title -> Some title

/// セルの中身を「名前と注釈の組」の列へ分解する。
/// 名前の区切りはbr要素と読点「、」の両方が使われているため、どちらでも区切る。
/// 脚注アンカーは直前の名前への注釈として付ける。
/// 存在しないページへの編集リンク(「?」の表示になる)は名前の一部ではないため無視する。
let private parseCell
    (notes: Map<string, string>)
    (cell: IElement)
    : (string * string option) list =
    let finalize (text: string, noteTexts: string list) segments =
        let name = text.Trim()

        // 名前も注釈も無い区切りは捨てるが、
        // 翻訳版の呼称のように注釈だけで説明される呼称もあるため、
        // 注釈があれば名前が空でも残す。
        if String.IsNullOrEmpty name && List.isEmpty noteTexts then
            segments
        else
            (name,
             (match List.rev noteTexts with
              | [] -> None
              | noteTexts -> Some(String.concat "、" noteTexts)))
            :: segments

    let segments, last =
        cell.ChildNodes
        |> Seq.fold
            (fun (segments, (text, noteTexts)) node ->
                match node with
                | :? IElement as element when element.ClassList.Contains "note_super" ->
                    match noteOfReference notes element with
                    | Some note -> segments, (text, note :: noteTexts)
                    | None -> segments, (text, noteTexts)
                | :? IElement as element when element.LocalName = "br" ->
                    finalize (text, noteTexts) segments, ("", [])
                | :? IElement as element when
                    (match element.GetAttribute "href" with
                     | null -> false
                     | href -> href.Contains "cmd=edit")
                    ->
                    segments, (text, noteTexts)
                | node ->
                    match node.TextContent.Split '、' |> Array.toList with
                    | [] -> segments, (text, noteTexts)
                    | first :: rest ->
                        rest
                        |> List.fold
                            (fun (segments, current) piece ->
                                finalize current segments, (piece, []))
                            (segments, (text + first, noteTexts)))
            ([], ("", []))

    List.rev (finalize last segments)

/// 呼称表のテーブル1つをレコードの列へ変換する。
/// キャラクター列は先頭行だけに置かれてrowspanで縦へ結合されているため、
/// 3セルの行はキャラクター列を読み飛ばし、2セルの行は相手と呼称の継続行として扱う。
/// 見出し行(thのみの行)は読み飛ばす。呼称が空の行はデータが無いので落とす。
let private parseTable
    (notes: Map<string, string>)
    (school: string)
    (club: string option)
    (character: string)
    (table: IElement)
    : Entry list =
    let rowEntries (calleeCell: IElement) (nameCell: IElement) =
        let calleeSegments = parseCell notes calleeCell
        let callee = calleeSegments |> List.map fst |> String.concat "、"

        let calleeNote =
            match List.choose snd calleeSegments with
            | [] -> None
            | noteTexts -> Some(String.concat "、" noteTexts)

        parseCell notes nameCell
        |> List.map (fun (name, note) ->
            { School = school
              Club = club
              Caller = character
              Callee = callee
              CalleeNote = calleeNote
              Name = name
              Note = note })

    table.QuerySelectorAll "tr"
    // trの走査は子孫全体に及ぶため、
    // セル内に入れ子のテーブルがあると内側の行まで拾ってしまう。
    // 最も近い祖先のtableが自分自身である行だけを外側のテーブルの行として扱う。
    |> Seq.filter (fun row -> obj.ReferenceEquals(row.Closest "table", table))
    |> Seq.toList
    |> List.collect (fun row ->
        let cells =
            row.Children |> Seq.filter (fun cell -> cell.LocalName = "td") |> Seq.toList

        match cells with
        | [] -> [] // 見出し行。
        | [ _; calleeCell; nameCell ] -> rowEntries calleeCell nameCell
        | [ calleeCell; nameCell ] -> rowEntries calleeCell nameCell
        | cells -> raise (RowShapeError(character = character, cellCount = List.length cells)))

/// パース済みDOMから呼称表の全レコードを取り出す。
/// 本文の見出し(h2学校 > h3部活 > h4キャラクター)を状態として辿り、
/// キャラクターの見出し配下の呼称表テーブルをレコード化する。
/// 目次のようにキャラクターの見出しより前にあるテーブルや、
/// コメント欄のように呼称表を持たないセクションは自然に読み飛ばされる。
/// wiki側の編集ミスで同じ行が2度書かれることがあるため、完全一致のレコードは1件へまとめる。
/// 1件も得られなかった場合はEntryNotFoundを送出する。
let parse (document: IDocument) : Entry list =
    let notes = noteMap document

    // 累積へ後ろから連結するとテーブルごとに累積全体のコピーが走るため、
    // テーブル単位の結果を先頭へ積み、最後に反転して平坦化することでO(レコード数)に保つ。
    let entries =
        document.QuerySelectorAll "#body h2, #body h3, #body h4, #body table"
        |> Seq.fold
            (fun (tableEntries, (school, club, character)) element ->
                match element.LocalName with
                | "h2" -> tableEntries, (Some(Extract.headingText element), None, None)
                | "h3" -> tableEntries, (school, Some(Extract.headingText element), None)
                | "h4" -> tableEntries, (school, club, Some(Extract.headingText element))
                | _ when
                    (match element.ParentElement with
                     | null -> false
                     | parent -> not (isNull (parent.Closest "table")))
                    ->
                    // 他のテーブルの内側にあるテーブルは外側の行のセルの一部なので、
                    // 独立したテーブルとしては読まない。
                    tableEntries, (school, club, character)
                | _ ->
                    match school, character with
                    | Some school, Some character ->
                        parseTable notes school club character element :: tableEntries,
                        (Some school, club, Some character)
                    | _ -> tableEntries, (school, club, character))
            ([], (None, None, None))
        |> fst
        |> List.rev
        |> List.concat
        |> List.distinct

    if List.isEmpty entries then
        raise EntryNotFound

    entries

/// HTML文字列から呼称表の全レコードを取り出す。
/// パースしてparseへ委譲する。挙動と失敗条件はそちらと同じ。
let parseHtml (html: string) : Entry list =
    use document = HtmlParser().ParseDocument html
    parse document

/// 名前に注釈があれば半角括弧で後ろへ添える。
let private nameWithNote (name: string) (note: string option) : string =
    match note with
    | Some note -> $"%s{name}(%s{note})"
    | None -> name

/// 呼ぶ側1人分のレコードを「相手のセルと呼称のセル」の組の列へまとめる。
/// 同じ相手への呼称は1行へまとめて読点で連結し、注釈は半角括弧で呼称の直後へ添える。
let private rowCells (callerEntries: Entry list) : (string * string) list =
    callerEntries
    |> List.groupBy (fun entry -> entry.Callee, entry.CalleeNote)
    |> List.map (fun ((callee, calleeNote), rowEntries) ->
        let names =
            rowEntries
            |> List.map (fun entry -> nameWithNote entry.Name entry.Note)
            |> String.concat "、"

        Markdown.escapeTableCell (nameWithNote callee calleeNote), Markdown.escapeTableCell names)

/// レコードの列からLLM参照用のMarkdown本文を組み立てる。
/// ページと同じ学校(h2) > 部活(h3) > キャラクター(h4)の階層へ組み直し、
/// 注釈は脚注ではなく呼称の直後の半角括弧に置いて、その場で条件を読めるようにする。
let toReferenceMarkdown (entries: Entry list) : string =
    let callerBlock (caller: string, callerEntries: Entry list) =
        let rows =
            rowCells callerEntries
            |> List.map (fun (calleeCell, namesCell) ->
                $"| %s{Markdown.escapeTableCell caller} | %s{calleeCell} | %s{namesCell} |")

        [ $"#### %s{caller}"; ""; "| キャラクター | 相手 | 呼称 |"; "| --- | --- | --- |" ]
        @ rows
        @ [ "" ]

    let clubBlock (club: string option, clubEntries: Entry list) =
        let callerBlocks = clubEntries |> List.groupBy _.Caller |> List.collect callerBlock

        match club with
        | Some club -> [ $"### %s{club}"; "" ] @ callerBlocks
        | None -> callerBlocks

    let schoolBlock (school: string, schoolEntries: Entry list) =
        [ $"## %s{school}"; "" ]
        @ (schoolEntries |> List.groupBy _.Club |> List.collect clubBlock)

    entries
    |> List.groupBy _.School
    |> List.collect schoolBlock
    |> String.concat "\n"

/// 指定した呼ぶ側のキャラクター1人分の呼称を、
/// role-playスキルへの埋め込みのようなLLM参照用のMarkdownテーブルへ組み立てる。
/// 呼ぶ側は指定した時点で自明なため、テーブルは相手と呼称の2列にする。
/// 該当するレコードが1件も無い場合はCallerNotFoundを送出する。
let toCallerMarkdown (caller: string) (entries: Entry list) : string =
    match entries |> List.filter (fun entry -> entry.Caller = caller) with
    | [] -> raise (CallerNotFound caller)
    | callerEntries ->
        let rows =
            rowCells callerEntries
            |> List.map (fun (calleeCell, namesCell) -> $"| %s{calleeCell} | %s{namesCell} |")

        [ "| 相手 | 呼称 |"; "| --- | --- |" ] @ rows @ [ "" ] |> String.concat "\n"

/// JSON直列化の設定。
/// F#のoption型をnullと値の対応で書けるようにJsonFSharpOptionsを使い、
/// 日本語をエスケープせずそのまま書いてdiffを読めるようにする。
/// FSharp.SystemTextJson v1.1以降の公式推奨であるフルーエントビルダーで組み込む。
let private serializerOptions =
    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    JsonFSharpOptions.Default().AddToJsonSerializerOptions options
    options

/// レコードの列を機械読み出し用のJSON文字列へ直列化する。
let toJson (document: Document) : string =
    JsonSerializer.Serialize(document, serializerOptions) + "\n"

/// JSON文字列をDocumentへ読み戻す。toJsonの逆変換。
/// 生成済みのappellation.jsonからwikiruへアクセスせずに呼称を読み出す用途で使う。
let ofJson (json: string) : Document =
    JsonSerializer.Deserialize<Document>(json, serializerOptions)
