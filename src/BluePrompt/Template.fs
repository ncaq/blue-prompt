/// テキストテンプレートへの値の差し込み。
/// 手書きのテンプレートの決まった位置へ、生成したMarkdownの断片を埋める。
/// 埋め込む値には表のようなMarkdownがそのまま入るため、
/// エスケープも整形もせず、渡された通りの文字列を置く。
module BluePrompt.Template

open System.Text.RegularExpressions

/// 差し込む位置を示すプレースホルダ。
/// `{{名前}}`の形で、テンプレートの中へ単独の段落として書く。
let private placeholderPattern = Regex(@"\{\{(\w+)\}\}", RegexOptions.Compiled)

/// プレースホルダの名前から、テンプレートへ書く表記を組み立てる。
/// テンプレートを組み立てるテストが、波括弧の数を自前で書かずに済むようにする。
let placeholder (name: string) : string = "{{" + name + "}}"

/// テンプレートのプレースホルダと差し込む値の食い違い。
type Mismatch =
    {
        /// テンプレートにあるのに値が渡されなかったプレースホルダの名前。
        Unresolved: string list
        /// 値を渡したのにテンプレートのどこにも現れなかった名前。
        Unused: string list
    }

/// テンプレートのプレースホルダへ値を差し込む。
/// テンプレートに現れる名前と渡した値の名前が完全に一致しない限り差し込まず、
/// どちらの向きで食い違ったのかを返す。
/// 片方向だけの検査では、
/// プレースホルダの書き間違いで値が黙って落ちることも、
/// テンプレートから消したプレースホルダへ値を渡し続けることも見逃すためである。
let render (values: Map<string, string>) (template: string) : Result<string, Mismatch> =
    let found =
        placeholderPattern.Matches template
        |> Seq.map (fun matched -> matched.Groups[1].Value)
        |> Set.ofSeq

    let given = Set.ofSeq (Map.keys values)

    if found = given then
        // 置換する文字列ではなく関数を渡すのは、
        // 差し込む値の中の`$1`のような並びが置換パターンとして解釈されないようにするため。
        // 走査の対象はテンプレートだけなので、
        // 差し込んだ値の中にプレースホルダの形があっても二重には展開されない。
        Ok(placeholderPattern.Replace(template, (fun matched -> values[matched.Groups[1].Value])))
    else
        Error
            { Unresolved = List.ofSeq (Set.difference found given)
              Unused = List.ofSeq (Set.difference given found) }

/// テンプレートのプレースホルダと差し込む値が食い違った時の、テンプレートのパスと内訳。
exception PlaceholderMismatch of path: string * mismatch: Mismatch

/// テンプレートのプレースホルダへ値を差し込み、食い違いを送出する。
/// 食い違いはどのファイルを直せばよいのかが分からないと対処できないため、
/// テンプレートの出どころのパスを添える。
/// テンプレートの内容から決まる値もあるため、読み込みは呼び出し側が行う。
let renderOrFail (path: string) (values: Map<string, string>) (template: string) : string =
    match render values template with
    | Ok rendered -> rendered
    | Error mismatch -> raise (PlaceholderMismatch(path, mismatch))
