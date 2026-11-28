/// 文字列をXParsecのパーサで読むための共通の道具。
/// 正規表現は量指定子と先読みと行頭行末の指定が1行に詰まって読みにくく、
/// 部品に名前を付けて使い回すこともできないため、
/// 文字列の形の判定と書き換えはパーサコンビネータで書く。
///
/// XParsecのパーサは正規表現と違って後戻りをしない。
/// 繰り返しは取れるだけ取ってから先へ進むので、
/// 繰り返しの文字の集合と直後の区切りが重ならないように組み立てる。
module BluePrompt.Parse

open System
open System.Text
open XParsec
open XParsec.Parsers
open XParsec.CharParsers

/// 状態を持たない、文字列を読むパーサ。
type TextParser<'a> = Parser<'a, char, unit, ReadableString>

/// 改行を跨がない空白1文字。
/// 行の中の空白を読み飛ばす時に、後続の行まで巻き込まないようにする。
let private isInlineSpace (c: char) : bool =
    Char.IsWhiteSpace c && c <> '\r' && c <> '\n'

/// 行内の空白を0個以上読み飛ばす。
let inlineSpaces: TextParser<unit> = skipManySatisfies isInlineSpace

/// 行内の空白を1個以上読み飛ばす。
let inlineSpaces1: TextParser<unit> = skipMany1Satisfies isInlineSpace

/// 改行も含む空白を0個以上読み飛ばす。
let whitespaces: TextParser<unit> = skipManySatisfies Char.IsWhiteSpace

/// 改行も含む空白を1個以上読み飛ばす。
let whitespaces1: TextParser<unit> = skipMany1Satisfies Char.IsWhiteSpace

/// 1文字以上の数字の並び。
let digits: TextParser<string> = many1Chars (satisfy Char.IsDigit)

/// 行の残りを改行の手前まで読む。空でもよい。
let restOfLine: TextParser<string> = manyChars (satisfy (fun c -> c <> '\n'))

/// 行頭であることを確かめる。入力は読み進めない。
/// 文書の先頭か、直前が改行の位置を行頭とする。
let lineStart: TextParser<unit> =
    fun reader ->
        if reader.Index = 0 || reader.Input.TryItem(reader.Index - 1) = ValueSome '\n' then
            preturn () reader
        else
            fail (Message "行頭ではありません") reader

/// 行末であることを確かめる。入力は読み進めない。
/// 文書の末尾か、直後が改行の位置を行末とする。
let lineEnd: TextParser<unit> =
    fun reader ->
        match reader.Peek() with
        | ValueNone
        | ValueSome '\n' -> preturn () reader
        | ValueSome _ -> fail (Message "行末ではありません") reader

/// 末尾の行内の空白を落とす。
let trimInlineEnd (text: string) : string =
    text.Substring(0, text.Length - (text |> Seq.rev |> Seq.takeWhile isInlineSpace |> Seq.length))

/// 文字列の先頭からパーサを適用する。後ろに続きが残っていてもよい。
let tryPrefix (p: TextParser<'a>) (text: string) : 'a option =
    match p (Reader.ofString text ()) with
    | Ok value -> Some value
    | Error _ -> None

/// 文字列の全体がパーサの読む形であれば結果を返す。
let tryWhole (p: TextParser<'a>) (text: string) : 'a option = tryPrefix (p .>> eof) text

/// 文字列の先頭がパーサの読む形で始まっているか。
let startsWith (p: TextParser<'a>) (text: string) : bool = Option.isSome (tryPrefix p text)

/// 一致した箇所1つ。開始位置と長さと、パーサが返した値。
type Found<'a> = { Index: int; Length: int; Value: 'a }

/// 文字列を先頭から走査して、パーサが読めた箇所を重ならないように列挙する。
/// 読めなかった位置は1文字進めてやり直す。
/// 何も読み進めない成功は、同じ位置で止まり続けないように一致として扱わない。
let findAll (p: TextParser<'a>) (text: string) : Found<'a> list =
    let reader = Reader.ofString text ()
    let found = ResizeArray()

    while not reader.AtEnd do
        let start = reader.Index

        match p reader with
        | Ok value when start < reader.Index ->
            found.Add
                { Index = start
                  Length = reader.Index - start
                  Value = value }
        | _ ->
            reader.Index <- start
            reader.Skip()

    List.ofSeq found

/// パーサが読めた最初の箇所。
let tryFind (p: TextParser<'a>) (text: string) : Found<'a> option = List.tryHead (findAll p text)

/// パーサが読めた箇所を、パーサが返した文字列へ置き換える。
/// 走査するのは元の文字列だけなので、置き換えた後の文字列がもう一度読まれることは無い。
let replace (p: TextParser<string>) (text: string) : string =
    let builder = StringBuilder()

    let last =
        findAll p text
        |> List.fold
            (fun position found ->
                builder.Append(text, position, found.Index - position).Append(found.Value)
                |> ignore

                found.Index + found.Length)
            0

    builder.Append(text, last, text.Length - last).ToString()

/// パーサが読めた箇所を取り除く。
let remove (p: TextParser<'a>) (text: string) : string = replace (p >>% "") text
