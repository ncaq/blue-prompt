/// ページ取得とMarkdown変換の合成。
module BluePrompt.Scrape

open System
open System.Threading.Tasks

/// URLのページをMarkdown化する。
let fetchMarkdown (url: Uri) : Task<string> =
    task {
        let! html = Page.fetchHtml url
        return! Pandoc.toMarkdown html
    }
