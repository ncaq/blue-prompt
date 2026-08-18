/// ブルーアーカイブ攻略有志Wiki(bluearchive.wikiru.jp)固有のページ取得とナレッジ化。
module BluePrompt.Wikiru

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Playwright
open System.Threading.Tasks

/// wikiruのページ名から記事URLを組み立てる。
let pageUri (pageName: string) : Uri =
    Uri $"https://bluearchive.wikiru.jp/?%s{Uri.EscapeDataString pageName}"

/// wikiruの記事からナレッジとして使う部分を抜き出す設定。
/// 本文(#body)と脚注(#note)を残し、サイトのヘッダ・サイドバー・フッタは含めない。
let contentQuery: Page.ContentQuery =
    { ContentSelectors = [ "#body"; "#note" ]
      RemoveSelectors =
        [
          // 見出しごとのページ内アンカー(†)と編集リンク。
          "a.anchor_super"
          // セクション末尾のページ上部へ戻るリンク。
          ".jumpmenu"
          // 目次。Markdownでは見出しをそのまま辿れる。
          "table.toc"
          // 折りたたみUI。ページ内ジャンプの五十音索引やコメント欄ルールに使われていて、
          // ジャンプできないMarkdownではどちらも要らない。
          // 折りたたみに本文が入っているページを扱うことになったら見直す。
          ".rgn-container"
          // 広告。
          ".sticky-ads"
          // コメント欄と投稿フォームとその部品(絵文字ピッカーなど)。
          // wiki独自コンテンツは扱わない方針のため。
          ".pcomment"
          "#pcomment-form"
          "div[class*='pcmt-']"
          // 画像。lazyload用プレースホルダのdata URIしか取れずノイズになる。
          "img" ]
      UnwrapLinks = true
      FlattenTables = true }

/// 最初の見出しより前を切り落とす。
/// wikiruの記事は本文が最初の見出しから始まり、
/// それより前は関連ページへのナビゲーションや注意書きで、ナレッジには要らない。
/// 見出しが無いMarkdownはそのまま返す。
let private trimPreamble (markdown: string) : string =
    match Regex.Match(markdown, @"^#{1,6} ", RegexOptions.Multiline) with
    | m when m.Success -> markdown[m.Index ..]
    | _ -> markdown

/// wikiruの脚注をGFMの脚注文法へ変換する。
/// 本文中の参照はリンクを外した後に「\*1」の形で残り、
/// 末尾の#note由来の定義は「\*1 本文」の行になっているので、
/// 定義行を「[^1]: 本文」へ、残った参照を「[^1]」へ書き換える。
let private convertFootnotes (markdown: string) : string =
    let withDefinitions =
        Regex.Replace(markdown, @"^\\\*(\d+)\s+(.*?)\s*$", "[^$1]: $2", RegexOptions.Multiline)

    Regex.Replace(withDefinitions, @"\\\*(\d+)", "[^$1]")

/// 変換後Markdownの後始末。
/// 最初の見出しより前のナビゲーションを切り落とし、
/// 中身を取り除いて残骸になったコメント欄の見出しを消し、
/// 脚注をGFMの文法へ変換し、連続する空行を1つへ潰す。
let cleanupMarkdown (markdown: string) : string =
    let withoutCommentHeading =
        Regex.Replace(trimPreamble markdown, @"^#{1,6} コメント(フォーム)?\s*$", "", RegexOptions.Multiline)

    let collapsed =
        Regex.Replace(convertFootnotes withoutCommentHeading, @"\n{3,}", "\n\n")

    collapsed.Trim() + "\n"

/// wikiruの記事ページをナレッジ用Markdownへ変換する。
/// pandocArgumentsでpandocの変換オプションを調整できる。
let fetchMarkdownWith
    (browser: IBrowser)
    (pandocArguments: string list)
    (pageName: string)
    : Task<string> =
    task {
        let! html = Page.fetchContentHtml browser (pageUri pageName) contentQuery
        let! markdown = Pandoc.toMarkdownWithArguments pandocArguments html
        return cleanupMarkdown markdown
    }

/// wikiruの記事ページを既定のpandoc引数でナレッジ用Markdownへ変換する。
let fetchMarkdown (browser: IBrowser) (pageName: string) : Task<string> =
    fetchMarkdownWith browser Pandoc.defaultMarkdownArguments pageName

/// ナレッジファイル先頭に付ける出典の表記。
/// Uri.ToStringはパーセントエンコードを解いた表示用文字列を返すため、リンクにはAbsoluteUriを使う。
let knowledgeHeader (pageName: string) : string =
    $"出典: [%s{pageName} - ブルーアーカイブ(ブルアカ)攻略有志Wiki](%s{(pageUri pageName).AbsoluteUri})\n\n"

/// wikiruの記事をMarkdown化し、出典ヘッダ付きのナレッジファイルとして書き出す。
/// スキルが参照するリファレンスファイルの生成の入口。
let writeKnowledge (browser: IBrowser) (pageName: string) (outputPath: string) : Task<unit> =
    task {
        let! markdown = fetchMarkdown browser pageName

        match Path.GetDirectoryName outputPath with
        | null
        | "" -> ()
        | directory -> Directory.CreateDirectory directory |> ignore

        do! File.WriteAllTextAsync(outputPath, knowledgeHeader pageName + markdown)
    }
