module BluePrompt.Test.WikiruSpec

open Xunit

[<Fact>]
let ``pageUriはページ名をパーセントエンコードしたURLを組み立てる`` () =
    Assert.Equal(
        "https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8",
        (BluePrompt.Wikiru.pageUri "キャラ呼称表").AbsoluteUri
    )

[<Fact>]
let ``cleanupMarkdownはコメント欄の節を丸ごと取り除く`` () =
    // コメントそのものは後から読み込まれるので残らないが、
    // 投稿のルールを畳んで置いているページがあり、見出しだけを消すと本文が残ってしまう。
    let markdown = "# 本文\n\n内容\n\n## コメントフォーム\n\n### ルールについて\n\n違反投稿は削除します\n"
    Assert.Equal("# 本文\n\n内容\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``コメント欄の次の節はまた残る`` () =
    let markdown = "## コメント\n\nルール\n\n## 小ネタ\n\n本文\n"
    Assert.Equal("## 小ネタ\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``コメント欄より深い小見出しでは節が終わらない`` () =
    // 節の終わりは同じ深さ以下の見出しなので、深い小見出しは節の中身として落ちる。
    let markdown = "## 小ネタ\n\n本文\n\n### コメント\n\nルール\n\n#### 子見出し\n\n子の本文\n"
    Assert.Equal("## 小ネタ\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``コメント欄より浅い見出しで節が終わる`` () =
    let markdown = "## 小ネタ\n\n本文\n\n### コメント\n\nルール\n\n## ボイス\n\nセリフ\n"
    Assert.Equal("## 小ネタ\n\n本文\n\n## ボイス\n\nセリフ\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``入れ子のコメント欄の見出しで節が早く終わらない`` () =
    // 深さを上書きすると内側の見出しの深さで節が終わり、外側の節の続きが残ってしまう。
    let markdown =
        "## 小ネタ\n\n本文\n\n## コメント\n\nルール\n\n### コメント\n\n入れ子のルール\n\n### 続きのルール\n\n続き\n"

    Assert.Equal("## 小ネタ\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``コメント欄へ紛れ込んだ脚注の定義は残る`` () =
    // 脚注の定義はページの末尾に置かれるためコメント欄の節に含まれるが、
    // 落とすと本文に残った参照が宙に浮く。
    let markdown = "## 小ネタ\n\n本文\\*1\n\n## コメント\n\nルール\n\n\\*1 注釈  \n"
    Assert.Equal("## 小ネタ\n\n本文[^1]\n\n[^1]: 注釈\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``cleanupMarkdownは連続する空行を1つへ潰す`` () =
    let markdown = "a\n\n\n\nb\n"
    Assert.Equal("a\n\nb\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``cleanupMarkdownは最初の見出しより前のナビゲーションを切り落とす`` () =
    let markdown = "一覧 \\| 索引\n\n最終更新日時:2026-08-15\n\n## 本文\n\n内容\n"
    Assert.Equal("## 本文\n\n内容\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``cleanupMarkdownは脚注をGFMの文法へ変換する`` () =
    let markdown = "# 本文\n\n| ノノミ | 十六夜ノノミさん\\*1 |\n\n\\*1 初対面時  \n"

    Assert.Equal(
        "# 本文\n\n| ノノミ | 十六夜ノノミさん[^1] |\n\n[^1]: 初対面時\n",
        BluePrompt.Wikiru.cleanupMarkdown markdown
    )

[<Fact>]
let ``外部リンクの跡として残った🌐は取り除かれる`` () =
    // wikiのJavaScriptが外部リンクの中へ付け足すアイコンは、
    // リンク外しの後にただの文字として残る。
    let markdown = "# h\n\n動画 🌐を開く\n"
    Assert.Equal("# h\n\n動画を開く\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``読点の前の空白は詰められる`` () =
    // セル内改行の結合で挟んだ読点の前に、元の空白テキストノード由来の空白が残ることがある。
    let markdown = "# h\n\n★2 ユウカ 、★3 ユウカ（体操服）\n"
    Assert.Equal("# h\n\n★2 ユウカ、★3 ユウカ（体操服）\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``区切り文字だけになった行は取り除かれる`` () =
    // 画像だけのリンクを「/」で並べたナビゲーションは、
    // 画像除去とリンク外しの後に区切り文字だけの行として残る。
    let markdown = "# h\n\n/\n\n本文\n\n/ /\n"
    Assert.Equal("# h\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``読点だけになった行は取り除かれる`` () =
    // セル内改行の結合で挟んだ読点は、
    // 続く要素が空行や空列の除去で消えると読点だけの行として孤立する。
    let markdown = "# h\n\n、\n\n本文\n"
    Assert.Equal("# h\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``水平線の行は区切り文字の除去では消えない`` () =
    let markdown = "# h\n\n本文\n\n---\n\n付記\n"
    Assert.Equal("# h\n\n本文\n\n---\n\n付記\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``脚注定義の変換は後続の段落を巻き込まない`` () =
    // 定義行にテキストが無い場合、正規表現が改行を跨いでマッチすると、
    // 無関係な次の段落が脚注定義へ吸い込まれてしまう。
    let markdown = "# h\n\n\\*1\n\n次の段落\n"
    Assert.Equal("# h\n\n[^1]\n\n次の段落\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``2桁の脚注も変換される`` () =
    let markdown = "# h\n\n本文\\*10\n\n\\*10 条件\n"
    Assert.Equal("# h\n\n本文[^10]\n\n[^10]: 条件\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``連続する脚注定義はそれぞれ変換される`` () =
    let markdown = "# h\n\n\\*1 初対面時  \n\\*2 変装中  \n"

    Assert.Equal("# h\n\n[^1]: 初対面時\n[^2]: 変装中\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``エスケープされていないアスタリスクは変換されない`` () =
    // pandocがエスケープしなかった*1は脚注由来ではないため素通りする。
    let markdown = "# h\n\n*1 これは脚注ではない\n"
    Assert.Equal("# h\n\n*1 これは脚注ではない\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``編集者向けの欄の案内は取り除かれる`` () =
    // ページのテンプレートが置いていく案内で、生徒についての事実ではない。
    let markdown = "## 小ネタ\n\nキャラの性格やストーリーの紹介を書く欄です。\n\n実際の小ネタ\n"
    Assert.Equal("## 小ネタ\n\n実際の小ネタ\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``案内の前に別の文がある行は残る`` () =
    // 落とすのは行全体が案内である場合だけで、
    // 事実の文に案内が続く行を丸ごと落としてはいけない。
    let markdown = "## 小ネタ\n\n本文の文です。ここから先を書く欄です。\n"

    Assert.Equal("## 小ネタ\n\n本文の文です。ここから先を書く欄です。\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``画像を失ったちびキャラの説明は取り除かれる`` () =
    // 画像を並べただけの折りたたみに付いていた説明で、画像の除去で指すものを失う。
    let markdown = "## 小ネタ\n\nちびキャラ/別ポーズ\n\n実際の小ネタ\n"
    Assert.Equal("## 小ネタ\n\n実際の小ネタ\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``ちびキャラで始まる本文の文は残る`` () =
    // 落としたいのはキャプション由来の名詞句だけで、
    // 小ネタの本文が同じ語で始まっても落としてはいけない。
    let markdown = "## 小ネタ\n\nちびキャラは制服と同じデザインです。\n"

    Assert.Equal("## 小ネタ\n\nちびキャラは制服と同じデザインです。\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``空行と同じ意味しか持たない行は取り除かれる`` () =
    // 実体参照のままの&nbsp;と中身の無い引用は、残すと見出しの中身として数えられてしまう。
    let markdown = "## 小ネタ\n\n&nbsp;\n\n>\n\n実際の小ネタ\n"
    Assert.Equal("## 小ネタ\n\n実際の小ネタ\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``中身を失った見出しは取り除かれる`` () =
    // 画像を並べただけの節は画像の除去で本文を失い、見出しだけが残る。
    let markdown = "## 小ネタ\n\n本文\n\n### 立ち絵バリエーション\n"
    Assert.Equal("## 小ネタ\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``小見出しが全て消えた見出しも取り除かれる`` () =
    let markdown = "## 小ネタ\n\n### 立ち絵バリエーション\n\n## ボイス\n\n本文\n"
    Assert.Equal("## ボイス\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``中身を持つ見出しは前の空の見出しに巻き込まれない`` () =
    let markdown = "## 小ネタ\n\n## ボイス\n\n本文\n"
    Assert.Equal("## ボイス\n\n本文\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``中身を持つ小見出しがあれば上位の見出しも残る`` () =
    let markdown = "## 小ネタ\n\n### 使用している銃の元ネタ\n\n解説\n"

    Assert.Equal("## 小ネタ\n\n### 使用している銃の元ネタ\n\n解説\n", BluePrompt.Wikiru.cleanupMarkdown markdown)

[<Fact>]
let ``studentContentQueryは折りたたみの中身を残し開閉UIだけ取り除く`` () =
    // 生徒ページでは絆ストーリー一覧やボイスなどの本文が折りたたみの中に入っている。
    // セレクタの引き算が表記変更で空振りすると、折りたたみごと本文が消える。
    let query = BluePrompt.Wikiru.studentContentQuery
    Assert.DoesNotContain(".rgn-container", query.RemoveSelectors)
    Assert.Contains(".rgn-button", query.RemoveSelectors)
    Assert.Contains(".rgn-description", query.RemoveSelectors)

[<Fact>]
let ``studentContentQueryは画像を除去せずaltのテキストへ置き換える`` () =
    // 素材や品物は画像で表現されていて、altに入っている名前が唯一の事実になる。
    let query = BluePrompt.Wikiru.studentContentQuery
    Assert.DoesNotContain("img", query.RemoveSelectors)
    Assert.True query.ReplaceImagesWithAlt

[<Fact>]
let ``structuredContentQueryはDOM構造を保ったままスクリプトとコメント欄を除く`` () =
    // Appellation.parseは脚注アンカーのhrefやrowspanを、
    // School.parseはカードのhrefやセル内のbrをDOMのまま読むため、
    // Markdown化前提の変形を掛けるとどちらのパースも壊れる。
    // 一方でスクリプト片や投稿フォーム由来の任意ユーザー入力の除去は、
    // contentQueryと同じ理由でこの経路にも必要。
    let query = BluePrompt.Wikiru.structuredContentQuery
    Assert.False query.UnwrapLinks
    Assert.False query.FlattenTables
    Assert.Contains("script", query.RemoveSelectors)
    Assert.Contains(".pcomment", query.RemoveSelectors)
    Assert.Contains("#pcomment-form", query.RemoveSelectors)

[<Fact>]
let ``structuredContentQueryの除去セレクタはcontentQueryの部分集合である`` () =
    // DOMを直接読む経路はMarkdown化固有の除去を持たないだけで、
    // ノイズの除去はcontentQueryと同じ集合を共有する。
    // ここが乖離するとこの経路だけスクリプト片や投稿フォームの除去が漏れる。
    let noise = BluePrompt.Wikiru.structuredContentQuery.RemoveSelectors
    let markdown = BluePrompt.Wikiru.contentQuery.RemoveSelectors
    Assert.All(noise, (fun selector -> Assert.Contains(selector, markdown)))

[<Fact>]
let ``filterSectionsはホワイトリストにあるh2セクションだけ残す`` () =
    let markdown = "## 基本情報\n\n名前\n\n## 運用考察\n\n考察本文\n\n## スキル\n\nEX\n"

    Assert.Equal(
        "## 基本情報\n\n名前\n\n## スキル\n\nEX\n",
        BluePrompt.Wikiru.filterSections [ "基本情報"; "スキル" ] markdown
    )

[<Fact>]
let ``filterSectionsはh3以下の小見出しをh2セクションごと扱う`` () =
    let markdown = "## スキル\n\n### EXスキル\n\n内容\n\n## 小ネタ\n\n### 元ネタ\n\n解説\n"

    Assert.Equal("## スキル\n\n### EXスキル\n\n内容\n", BluePrompt.Wikiru.filterSections [ "スキル" ] markdown)

[<Fact>]
let ``studentSectionTitlesは小ネタを残す`` () =
    // 小ネタはゲーム内外の事実のまとめで、事実には著作権が発生しないため、
    // 運用考察などの執筆者の考察とは扱いが異なる。
    // 文言の見直しで落ちるとナレッジから静かに消えるため、実リストへ確かめる。
    Assert.Contains("小ネタ", BluePrompt.Wikiru.studentSectionTitles)

[<Fact>]
let ``studentSectionTitlesでの選別で小ネタのh3小見出しも残る`` () =
    let markdown = "## 小ネタ\n\n### 使用している銃の元ネタ\n\n解説\n\n## 運用考察\n\n考察本文\n"

    Assert.Equal(
        "## 小ネタ\n\n### 使用している銃の元ネタ\n\n解説\n",
        BluePrompt.Wikiru.filterSections BluePrompt.Wikiru.studentSectionTitles markdown
    )

[<Fact>]
let ``filterSectionsは最初のh2より前の部分を残す`` () =
    let markdown = "前書き\n\n## 基本情報\n\n名前\n"
    Assert.Equal("前書き\n\n## 基本情報\n\n名前\n", BluePrompt.Wikiru.filterSections [ "基本情報" ] markdown)

[<Fact>]
let ``filterSectionsは参照ごと落ちた脚注定義を取り除く`` () =
    // 落ちたセクションだけが参照していた脚注の定義を残すと、宙に浮いた脚注になる。
    let markdown = "## 基本情報\n\n名前\n\n## 運用考察\n\n考察[^1]\n\n[^1]: 補足\n"
    Assert.Equal("## 基本情報\n\n名前\n", BluePrompt.Wikiru.filterSections [ "基本情報" ] markdown)

[<Fact>]
let ``filterSectionsは残った本文から参照される脚注定義を文書末尾に保持する`` () =
    // 脚注定義は落ちるセクションの側に置かれていても、参照が残っていれば保持する。
    let markdown = "## 基本情報\n\n名前[^1]\n\n## コメント\n\n[^1]: 補足\n"

    Assert.Equal(
        "## 基本情報\n\n名前[^1]\n\n[^1]: 補足\n",
        BluePrompt.Wikiru.filterSections [ "基本情報" ] markdown
    )

[<Fact>]
let ``trimGameplayDetailsは入手方法から次のh2までを切り落とす`` () =
    // 基本情報セクションの入手方法・ステータス・装備は性能データで、
    // role-playの参照にはプロフィールと紹介文とボイスだけが要る。
    let markdown =
        "## 基本情報\n\n| 名前 | ユウカ |\n\n**基本情報**\n\n紹介文\n\n"
        + "**入手方法**\n\n通常募集\n\n**ステータス(初期値/MAX値)**\n\n| HP | 3146 |\n\n"
        + "## ボイス\n\n| 獲得 | セリフ |\n"

    Assert.Equal(
        "## 基本情報\n\n| 名前 | ユウカ |\n\n**基本情報**\n\n紹介文\n\n## ボイス\n\n| 獲得 | セリフ |\n",
        BluePrompt.Wikiru.trimGameplayDetails markdown
    )

[<Fact>]
let ``trimGameplayDetailsは続くh2が無ければ末尾まで切り落とす`` () =
    let markdown = "## 基本情報\n\n紹介文\n\n**入手方法**\n\n通常募集\n"
    Assert.Equal("## 基本情報\n\n紹介文\n", BluePrompt.Wikiru.trimGameplayDetails markdown)

[<Fact>]
let ``trimGameplayDetailsは入手方法が無ければ何も変えない`` () =
    let markdown = "## 基本情報\n\n紹介文\n\n## ボイス\n\n| 獲得 | セリフ |\n"
    Assert.Equal(markdown, BluePrompt.Wikiru.trimGameplayDetails markdown)

[<Fact>]
let ``studentSkillMarkdownはフロントマターと出典とナレッジを含む`` () =
    let skill =
        BluePrompt.Wikiru.studentSkillMarkdown "character-yuuka" "ユウカ" "## 基本情報\n"

    Assert.StartsWith("---\nname: character-yuuka\n", skill)
    Assert.Contains("\nuser-invocable: false\n", skill)
    Assert.Contains("https://bluearchive.wikiru.jp/?%E3%83%A6%E3%82%A6%E3%82%AB", skill)
    Assert.Contains("## 基本情報", skill)

[<Fact>]
let ``sectionTitlesはh2の見出しだけを現れる順に拾う`` () =
    let markdown = "前書き\n\n## 基本情報\n\n### 内訳\n\n本文\n\n## ボイス\n"

    Assert.Equal<string list>([ "基本情報"; "ボイス" ], BluePrompt.Wikiru.sectionTitles markdown)

[<Fact>]
let ``sectionTitlesは同じ見出しを2度並べない`` () =
    // ページ側の事情で見出しが重複しても、「ボイス・ボイス」のような説明文にしない。
    let markdown = "## ボイス\n\n本文\n\n## 基本情報\n\n## ボイス\n"

    Assert.Equal<string list>([ "ボイス"; "基本情報" ], BluePrompt.Wikiru.sectionTitles markdown)

[<Fact>]
let ``studentSkillMarkdownのデータの構造は実際にある節だけを並べる`` () =
    // 愛用品の節を持たない衣装で、存在しない節を存在すると宣言しないことを確かめる。
    let skill =
        BluePrompt.Wikiru.studentSkillMarkdown
            "character-kotori-cheer-squad"
            "コトリ（応援団）"
            "## 基本情報\n\n## スキル\n\n## ボイス\n"

    Assert.Contains("- セクションは基本情報・スキル・ボイスです\n", skill)
    Assert.DoesNotContain("愛用品", skill)

[<Fact>]
let ``studentSkillMarkdownは節が1つも無ければ止まる`` () =
    // 何が載っているかを述べない壊れた説明のスキルを書き出さない。
    let error =
        Assert.Throws<BluePrompt.Wikiru.StudentSectionNotFound>(fun () ->
            BluePrompt.Wikiru.studentSkillMarkdown "character-yuuka" "ユウカ" "本文だけ\n"
            |> ignore)

    Assert.Equal("ユウカ", error.pageName)

[<Fact>]
let ``sourceHeaderはURLをそのまま使いページ名をデコードで復元する`` () =
    let header =
        BluePrompt.Wikiru.sourceHeader (
            System.Uri "https://bluearchive.wikiru.jp/?%E3%83%A6%E3%82%A6%E3%82%AB"
        )

    Assert.Contains("[ユウカ - ", header)
    Assert.Contains("(https://bluearchive.wikiru.jp/?%E3%83%A6%E3%82%A6%E3%82%AB)", header)

[<Fact>]
let ``sourceHeaderはクエリの無いURLではURL全体を表示名にする`` () =
    // ページ名を復元できないURLでも表記が空欄にならないようにする。
    let header =
        BluePrompt.Wikiru.sourceHeader (System.Uri "https://bluearchive.wikiru.jp/")

    Assert.Contains("[https://bluearchive.wikiru.jp/ - ", header)

[<Fact>]
let ``knowledgeHeaderは出典URLを含む`` () =
    Assert.Contains(
        "https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8",
        BluePrompt.Wikiru.knowledgeHeader "キャラ呼称表"
    )
