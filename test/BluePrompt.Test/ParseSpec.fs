module BluePrompt.Test.ParseSpec

open Xunit
open XParsec
open XParsec.CharParsers
open BluePrompt

[<Fact>]
let ``何も読まずに成功するパーサでもfindAllは止まって何も返さない`` () =
    // 0文字の成功を一致として扱うと、同じ位置で止まり続けて終わらない。
    Assert.Empty(Parse.findAll Parse.inlineSpaces "abc")

[<Fact>]
let ``findAllは読めた箇所の位置と長さと値を重ならないように返す`` () =
    let found = Parse.findAll Parse.digits "a12b345"

    Assert.Equal<(int * int * string) list>(
        [ 1, 2, "12"; 4, 3, "345" ],
        found |> List.map (fun found -> found.Index, found.Length, found.Value)
    )

[<Fact>]
let ``tryFindは最初の一致を返す`` () =
    let found = Parse.tryFind Parse.digits "a12b345"
    Assert.Equal(Some(1, "12"), found |> Option.map (fun found -> found.Index, found.Value))

[<Fact>]
let ``tryFindは一致が無ければNoneを返す`` () =
    Assert.Equal(None, Parse.tryFind Parse.digits "abc")

[<Fact>]
let ``replaceは置き換えた後の文字列を読み直さない`` () =
    // 置き換えた結果をもう一度読むと、aが増え続けて終わらない。
    Assert.Equal("aabaa", Parse.replace (pchar 'a' >>% "aa") "aba")

[<Fact>]
let ``removeは読めた箇所だけを取り除く`` () =
    Assert.Equal("ab", Parse.remove Parse.digits "a12b345")

[<Fact>]
let ``lineStartは文書の先頭と改行の直後だけを行頭とする`` () =
    let found = Parse.findAll (Parse.lineStart >>. pchar 'x') "x\nyx\nx"
    Assert.Equal<int list>([ 0; 5 ], found |> List.map _.Index)

[<Fact>]
let ``lineEndは改行の手前と文書の末尾を行末とする`` () =
    let found = Parse.findAll (pchar 'x' .>> Parse.lineEnd) "x\nxy\nx"
    Assert.Equal<int list>([ 0; 5 ], found |> List.map _.Index)

[<Fact>]
let ``anyLineは空行も1行として読み文書の末尾では失敗する`` () =
    Assert.Equal(Some "", Parse.tryPrefix Parse.anyLine "\n次の行")
    Assert.Equal(None, Parse.tryPrefix Parse.anyLine "")

/// 「受け付ける」という行だけを受け付けるlineChoose。
let private chooseAccepted: Parse.TextParser<string> =
    Parse.lineChoose (fun line -> if line = "受け付ける" then Some line else None)

[<Fact>]
let ``lineChooseは受け付けなかった行を読み進めない`` () =
    // `<|>`は右のパーサの前に読み位置を自分で戻すので、
    // 組み合わせた結果を見ても、lineChooseが位置を戻したのかは分からない。
    // Readerへ直接適用して、失敗した後の位置を見る。
    let reader = Reader.ofString "別の行\n続き" ()

    Assert.True(Result.isError (chooseAccepted reader))
    Assert.Equal(0, reader.Index)

[<Fact>]
let ``lineChooseは受け付けた行を改行ごと読み進める`` () =
    let reader = Reader.ofString "受け付ける\n続き" ()

    Assert.Equal(Ok "受け付ける", chooseAccepted reader |> Result.mapError ignore)
    Assert.Equal("受け付ける\n".Length, reader.Index)

[<Fact>]
let ``trimInlineEndは末尾の行内の空白だけを落とす`` () =
    Assert.Equal(" a b", Parse.trimInlineEnd " a b \t")
    Assert.Equal("a\n", Parse.trimInlineEnd "a\n")
    Assert.Equal("", Parse.trimInlineEnd "  ")
