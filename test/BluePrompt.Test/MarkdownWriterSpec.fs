module BluePrompt.Test.MarkdownWriterSpec

open System
open Markdig
open Xunit
open BluePrompt.Article

/// 脚注を持たない文書として書き出す。
let private render (blocks: Block list) : string =
    BluePrompt.MarkdownWriter.render { Blocks = blocks; Footnotes = [] }

/// 書き出したMarkdownをMarkdigで読み戻して、表示される文字だけにする。
let private roundTrip (blocks: Block list) : string =
    let pipeline = MarkdownPipelineBuilder().UseAdvancedExtensions().Build()

    Markdown.ToPlainText(render blocks, pipeline).TrimEnd '\n'

[<Theory>]
[<InlineData("a_b_c と *強調* ではない")>]
[<InlineData("[リンク] ではない <tag> でもない")>]
[<InlineData("# 見出しではない")>]
[<InlineData("- 箇条書きではない")>]
[<InlineData("1. 番号ではない")>]
[<InlineData("> 引用ではない")>]
[<InlineData("バックスラッシュ\\と`コード`")>]
let ``構造に見える文字は書き出して読み戻しても文字のまま`` (text: string) =
    Assert.Equal(text, roundTrip [ Paragraph [ Text text ] ])

[<Fact>]
let ``改行の直後の行頭の形もエスケープされる`` () =
    // 行を分けると全ての行の先頭が行頭になる。
    let blocks = [ Paragraph [ Text "前の行"; LineBreak; Text "# 見出しではない" ] ]
    Assert.Equal("前の行\n\\# 見出しではない\n", render blocks)

[<Fact>]
let ``区切り線に見える行は段落の下線として読まれない`` () =
    let blocks = [ Paragraph [ Text "本文"; LineBreak; Text "---" ] ]
    let rendered = render blocks
    Assert.DoesNotContain("\n---\n", rendered)

[<Fact>]
let ``脚注は参照と末尾の定義として書き出される`` () =
    let article: Article =
        { Blocks = [ Paragraph [ Text "本文"; FootnoteReference "4"; Text "1955年" ] ]
          Footnotes = [ { Label = "4"; Content = [ Text "補足" ] } ] }

    // 参照の直後に数字が続いても、番号は要素として持っているので混ざらない。
    Assert.Equal("本文[^4]1955年\n\n[^4]: 補足\n", BluePrompt.MarkdownWriter.render article)

[<Fact>]
let ``リンクと画像と強調を書き出す`` () =
    let blocks =
        [ Paragraph
              [ Link(Uri "https://example.com/post", [ Text "外部" ])
                Image("素材", Uri "https://example.com/a.png")
                Strong [ Text "太字" ]
                Strikethrough [ Text "取り消し" ] ] ]

    Assert.Equal(
        "[外部](https://example.com/post)![素材](https://example.com/a.png)**太字**~~取り消し~~\n",
        render blocks
    )

[<Fact>]
let ``項目の題名は直前の見出しの1つ下の見出しになる`` () =
    let content (text: string) : CellContent =
        { Inlines = [ Text text ]; Blocks = [] }

    let blocks =
        [ Heading(2, [ Text "スキル" ])
          Items
              [ { Title = Some "EXスキル"
                  Items = [ [ Key(content "1"); Value("効果", content "攻撃") ] ] } ] ]

    Assert.Equal("## スキル\n\n### EXスキル\n\n- 1: 効果: 攻撃\n", render blocks)
