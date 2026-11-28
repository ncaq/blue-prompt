module BluePrompt.Test.RolePlaySpec

open System
open System.IO
open Xunit
open BluePrompt.Article
open BluePrompt.Character
open BluePrompt.RolePlay

/// 衣装のページのURL。
let private pageSource (pageName: string) : Uri =
    Uri("https://bluearchive.wikiru.jp/?" + Uri.EscapeDataString pageName)

/// ボイスの節だけを持つ衣装を組み立てる。
/// セリフは脚注を1つ参照し、その脚注の本文をnoteにする。
/// 脚注のラベルはwikiruのページごとに1から数えるので、どの衣装でも"1"にする。
let private costume (pageName: string) (line: string) (note: string) : Costume =
    { PageName = pageName
      Source = pageSource pageName
      Character =
        { Preamble = []
          Sections =
            [ { Title = [ Text "ボイス" ]
                Content =
                  Voice
                      [ { Title = None
                          Lines =
                            [ { Key = [ Text "ログイン" ]
                                Value = [ Text line; FootnoteReference "1" ] } ] } ] } ]
          Footnotes = [ { Label = "1"; Content = [ Text note ] } ] } }

[<Fact>]
let ``衣装は出典のページ名の見出しと出典の行の下へ中身を収める`` () =
    // 参照データの見出しは二段目から始まるので、一段目の衣装の見出しの下へそのまま入る。
    // 節と節の間に空行が無いと、見出しが前の本文へ吸着してMarkdownが壊れる。
    let url = (pageSource "ユウカ").AbsoluteUri

    Assert.Equal(
        "# 衣装: ユウカ\n\n"
        + $"出典: [ユウカ - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{url})\n\n"
        + "## ボイス\n\n"
        + "- ログイン: おはよう[^1]\n\n"
        + "[^1]: 注",
        costumesMarkdown [ costume "ユウカ" "おはよう" "注" ]
    )

[<Fact>]
let ``衣装を繋ぐと脚注のラベルが全体の通し番号になる`` () =
    // 衣装はどれも脚注を1から数えるので、そのまま繋ぐと定義が重なり、
    // GFMでは後の衣装の脚注が捨てられて、参照が前の衣装の脚注を指してしまう。
    let markdown =
        costumesMarkdown [ costume "ユウカ" "通常のセリフ" "通常の注"; costume "ユウカ（体操服）" "体操服のセリフ" "体操服の注" ]

    Assert.Contains("通常のセリフ[^1]", markdown)
    Assert.Contains("[^1]: 通常の注", markdown)
    Assert.Contains("体操服のセリフ[^2]", markdown)
    Assert.Contains("[^2]: 体操服の注", markdown)

[<Fact>]
let ``取り消し線は本文の一部として残る`` () =
    // 小ネタの取り消し線も書き手の文章の一部なので、生徒個別スキルと同じく残す。
    let character: BluePrompt.Character.Character =
        { Preamble = []
          Sections =
            [ { Title = [ Text "小ネタ" ]
                Content = Trivia [ Paragraph [ Text "本文。"; Strikethrough [ Text "ツッコミ" ] ] ] } ]
          Footnotes = [] }

    let markdown =
        costumesMarkdown
            [ { PageName = "ユウカ"
                Source = pageSource "ユウカ"
                Character = character } ]

    Assert.EndsWith("## 小ネタ\n\n本文。~~ツッコミ~~", markdown)

[<Fact>]
let ``生徒個別スキルの型のJSONは読み戻すと元の衣装になる`` () =
    // role-playスキルはwikiruへアクセスせず、このJSONだけから衣装を組み立てる。
    let original = costume "ユウカ" "おはよう" "注"

    let document: BluePrompt.Wikiru.CharacterDocument =
        { PageName = original.PageName
          Source = original.Source.AbsoluteUri
          Character = original.Character }

    let json = BluePrompt.JsonCodec.serialize document
    Assert.Equal(original, ofCharacterDocument (BluePrompt.Wikiru.ofCharacterJson json))

[<Fact>]
let ``一段目の見出しを持つ衣装はCostumeHeadingErrorになる`` () =
    // 衣装の節は一段目の見出しを立てた下へ中身を収めるため、
    // 中身が一段目の見出しを持つと節の切れ目が消えて、
    // 以降の内容が別の衣装の兄弟として並ぶ。
    let original = costume "ユウカ" "おはよう" "注"

    let broken =
        { original with
            Costume.Character =
                { original.Character with
                    Preamble = [ Heading(1, [ Text "基本情報" ]) ] } }

    let error =
        Assert.Throws<CostumeHeadingError>(fun () -> costumesMarkdown [ broken ] |> ignore)

    match error :> exn with
    | CostumeHeadingError pageName -> Assert.Equal("ユウカ", pageName)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

/// 代表的な発言を並べたquoteディレクトリを持つスキルのディレクトリを作る。
let private makeQuoteDirectory (files: (string * string) list) : string =
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let quoteDirectory = Path.Combine(directory, "quote")
    Directory.CreateDirectory quoteDirectory |> ignore

    for name, content in files do
        File.WriteAllText(Path.Combine(quoteDirectory, name), content)

    directory

[<Fact>]
let ``readQuotesはファイル名の順に発言を読む`` () =
    let directory =
        makeQuoteDirectory [ "seminar.md", "## 会計の話\n\n本文\n"; "budget.md", "## 予算の話\n\n本文\n" ]

    Assert.Equal<string list>(
        [ "## 予算の話\n\n本文"; "## 会計の話\n\n本文" ],
        (readQuotes directory).GetAwaiter().GetResult()
    )

[<Fact>]
let ``quoteディレクトリの無いスキルは発言を持たない`` () =
    // 発言は手書きなので、用意していない生徒が居る。
    let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory directory |> ignore

    Assert.Empty((readQuotes directory).GetAwaiter().GetResult())

[<Fact>]
let ``見出しから始まらない発言はQuoteShapeErrorになる`` () =
    // 見出しが無いと、節へ並べた時に発言と発言の切れ目が消える。
    let directory = makeQuoteDirectory [ "budget.md", "本文だけ\n" ]

    let error =
        Assert.Throws<QuoteShapeError>(fun () ->
            (readQuotes directory).GetAwaiter().GetResult() |> ignore)

    match error :> exn with
    | QuoteShapeError path -> Assert.Equal("budget.md", Path.GetFileName path)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``quotesMarkdownは見出しごと節を組み立てる`` () =
    // 発言と発言の間に空行が入らないと、見出しが前の本文へ吸着してMarkdownが壊れる。
    // 区切りは2件以上ある時にしか現れないので、2件を渡して全文を固定する。
    let markdown = quotesMarkdown "ユウカ" [ "## 予算の話\n\n本文"; "## 会計の話\n\n本文" ]

    Assert.Equal(
        "# 代表的な発言\n\n"
        + "作中でユウカが実際に話した発言です。\n"
        + "ボイスの短いセリフだけでは掴めない、話の運び方と長い語りの組み立てが分かるものを選んでいます。\n"
        + "そのまま繰り返さず、言葉選びと話の展開の手本として使ってください。\n\n"
        + "## 予算の話\n\n本文\n\n"
        + "## 会計の話\n\n本文",
        markdown
    )

[<Fact>]
let ``発言が無ければ節ごと消える`` () =
    // 見出しだけが残ると、中身の無い節を読ませることになる。
    Assert.Equal("", quotesMarkdown "ユウカ" [])

[<Fact>]
let ``knowledgeSkillsMarkdownは生徒のナレッジのスキル名を並べる`` () =
    // 全てのスキルが参照するcharacter-appellationは、本文では別の文が扱うので除く。
    let names = [ "character-yuuka"; "character-yuuka-track"; "character-appellation" ]

    Assert.Equal(
        "- character-yuuka\n- character-yuuka-track",
        knowledgeSkillsMarkdown "character.md" names
    )

[<Fact>]
let ``生徒のナレッジが無いとKnowledgeSkillNotFoundになる`` () =
    // 参照先を挙げない壊れた文を書き出さない。
    let error =
        Assert.Throws<KnowledgeSkillNotFound>(fun () ->
            knowledgeSkillsMarkdown "character.md" [ "character-appellation" ] |> ignore)

    match error :> exn with
    | KnowledgeSkillNotFound path -> Assert.Equal("character.md", path)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

/// テンプレートの全てのプレースホルダを、
/// 差し込まれた値だけが並ぶ形で書き出すテンプレート。
/// 配線の取り違えがそのまま出力の順序の違いとして現れる。
let private template =
    "{{caller}}\n{{character}}\n{{playing}}\n{{appellation}}\n"
    + "{{costumes}}\n{{quotes}}\n{{knowledgeSkills}}\n"

/// キャラ呼称表のページのURL。
let private appellationSource =
    "https://bluearchive.wikiru.jp/?" + Uri.EscapeDataString "キャラ呼称表"

/// 呼称表を1件だけ持つ文書を組み立てる。
let private document (caller: string) : BluePrompt.Appellation.Document =
    { Source = appellationSource
      Entries =
        [ { School = "ミレニアム"
            Club = Some "セミナー"
            Caller = caller
            Callee = "先生"
            CalleeNote = None
            Name = "先生"
            Note = None } ] }

/// 本文を持つcharacter.mdのフロントマター。
let private characterOf (content: string) : BluePrompt.OpenWebui.Frontmatter =
    BluePrompt.OpenWebui.parseFrontmatter "character.md" content

/// テンプレートへ差し込む値を一通り持つ、ユウカのrole-playスキル。
let private yuuka: RolePlay =
    { Caller = "ユウカ"
      CharacterPath = "character.md"
      Character =
        characterOf "---\nname: yuuka\ndescription: d\nknowledge: character-yuuka\n---\n\n位置付け。\n"
      Costumes = [ costume "ユウカ" "おはよう" "注" ]
      Quotes = []
      Appellation = document "ユウカ" }

[<Fact>]
let ``renderSkillはフロントマターと差し込んだ本文を並べる`` () =
    let character =
        characterOf (
            "---\nname: yuuka\ndescription: Role-play as Yuuka\n"
            + "knowledge: character-yuuka, character-appellation\n---\n\n早瀬ユウカの位置付け。\n"
        )

    let quotes = [ "## 予算の話\n\n本文" ]

    let skill =
        renderSkill
            "SKILL.template.md"
            template
            { yuuka with
                Character = character
                Quotes = quotes }

    Assert.Equal(
        "---\nname: yuuka\ndescription: Role-play as Yuuka\n"
        + "knowledge: character-yuuka, character-appellation\n---\n"
        + "\nユウカ\n早瀬ユウカの位置付け。\n"
        + playingRules
        + $"\n出典: [キャラ呼称表 - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{appellationSource})\n"
        + "\n| 相手 | 呼称 |\n| --- | --- |\n| 先生 | 先生 |\n\n"
        + costumesMarkdown yuuka.Costumes
        + "\n"
        + quotesMarkdown "ユウカ" quotes
        + "\n- character-yuuka\n",
        skill
    )

[<Fact>]
let ``renderSkillは呼称表に行が無い生徒でも表が無いことを書いて生成する`` () =
    // 実装されたばかりの生徒は、wikiruの呼称表へ行が足されるまでレコードを持たない。
    let skill =
        renderSkill
            "SKILL.template.md"
            template
            { yuuka with
                Appellation = document "ノア" }

    Assert.Contains("呼称表にはまだユウカの行がありません。", skill)
    Assert.DoesNotContain("| 相手 | 呼称 |", skill)

[<Fact>]
let ``本文の無いcharacter.mdはCharacterBodyNotFoundになる`` () =
    // 衣装とナレッジは0件で止まるのに本文だけ素通りすると、
    // その生徒の位置付けが黙って落ちた本文が生成される。
    let error =
        Assert.Throws<CharacterBodyNotFound>(fun () ->
            renderSkill
                "SKILL.template.md"
                template
                { yuuka with
                    Character =
                        characterOf
                            "---\nname: yuuka\ndescription: d\nknowledge: character-yuuka\n---\n" }
            |> ignore)

    match error :> exn with
    | CharacterBodyNotFound path -> Assert.Equal("character.md", path)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``衣装が無いとCostumeNotFoundになる`` () =
    // 衣装の節を1つも持たない本文を書き出さない。
    let error =
        Assert.Throws<CostumeNotFound>(fun () ->
            renderSkill "SKILL.template.md" template { yuuka with Costumes = [] } |> ignore)

    match error :> exn with
    | CostumeNotFound caller -> Assert.Equal("ユウカ", caller)
    | unexpected -> failwith $"想定外の例外です: %O{unexpected}"

[<Fact>]
let ``renderSkillはテンプレートのプレースホルダが欠けると止まる`` () =
    // 差し込むはずの内容が黙って落ちないようにする。
    Assert.Throws<BluePrompt.Template.PlaceholderMismatch>(fun () ->
        renderSkill "SKILL.template.md" "{{caller}}\n" yuuka |> ignore)
    |> ignore
