/// ページ取得とMarkdown変換の合成。
module BluePrompt.Scrape

open System
open Microsoft.Playwright
open System.Threading.Tasks

/// 既存のブラウザを使ってURLのページをMarkdown化する。
/// 複数URLを処理する時はこちらでブラウザを再利用する。
let toMarkdown (browser: IBrowser) (url: Uri) : Task<string> =
    task {
        let! html = Page.fetchHtml browser url
        return! Pandoc.toMarkdown html
    }

/// ブラウザの起動から破棄まで込みでURLのページをMarkdown化する。
/// 単一URLを手軽に変換する入口。
let fetchMarkdown (url: Uri) : Task<string> =
    Browser.withBrowser (fun browser -> toMarkdown browser url)
