/// webページのHTML取得。
module BluePrompt.Page

open System
open Microsoft.Playwright
open System.Threading.Tasks

/// HTTPステータスが成功以外だった時のURLとステータスコード。
exception FetchError of url: Uri * status: int

/// コンテンツ抽出でどのセレクタも要素に一致しなかった時のURLとセレクタ一覧。
exception ContentNotFound of url: Uri * selectors: string list

/// コンテンツ抽出の設定。
/// ヘッダやサイドバーや広告を除いた本文だけをナレッジ用に抜き出す用途を想定している。
type ContentQuery =
    {
        /// 抽出する要素のCSSセレクタ。
        /// 複数指定した場合はセレクタの列挙順に一致要素のHTMLを連結する。
        ContentSelectors: string list
        /// 抽出前にDOMから取り除く要素のCSSセレクタ。
        RemoveSelectors: string list
        /// aタグを外して中身だけ残すか。
        /// ページ内アンカーや相対リンクはページの外へ持ち出すと辿れず、ノイズにしかならない。
        UnwrapLinks: bool
        /// テーブルのrowspan/colspanを個別セルへ複製展開し、セル内のbrを区切り文字に置換するか。
        /// GFMのパイプテーブルは結合セルもセル内改行も表現できず、
        /// pandocが変換を諦めてテーブル全体を[TABLE]という文字列へ潰してしまうため。
        FlattenTables: bool
    }

/// URLへ遷移してloadイベントを待ち、開いたページをactionへ渡す。
/// HTTPステータスが成功以外の場合はFetchErrorを送出する。
/// file:等によるローカル読み出しを防ぐため、スキームはhttpとhttpsに限定する。
let private withPage (browser: IBrowser) (url: Uri) (action: IPage -> Task<'T>) : Task<'T> =
    task {
        if url.Scheme <> Uri.UriSchemeHttp && url.Scheme <> Uri.UriSchemeHttps then
            raise (ArgumentException($"http/https以外のスキームは扱えません: %O{url}", nameof url))

        use! context = browser.NewContextAsync()
        let! page = context.NewPageAsync()
        let! response = page.GotoAsync url.AbsoluteUri

        // about:blankへの遷移などではresponseがnullになるため、その場合は検査しない。
        match response with
        | null -> ()
        | response when not response.Ok -> raise (FetchError(url = url, status = response.Status))
        | _ -> ()

        return! action page
    }

/// URLへ遷移し、loadイベント後のDOM全体をHTML文字列で返す。
/// browserを引数に取ることで1つのブラウザで複数ページを取得できる。
/// エラー条件はwithPageと同じ。
let fetchHtml (browser: IBrowser) (url: Uri) : Task<string> =
    withPage browser url (fun page -> page.ContentAsync())

/// 一致した要素をDOMから取り除くスクリプト。
let private removeElementsScript =
    "(elements) => elements.forEach((element) => element.remove())"

/// 一致した要素のタグを外して子ノードだけ残すスクリプト。
let private unwrapElementsScript =
    "(elements) => elements.forEach((element) => element.replaceWith(...element.childNodes))"

/// 一致した要素それぞれのouterHTMLを列挙するスクリプト。
let private outerHtmlScript =
    "(elements) => elements.map((element) => element.outerHTML)"

/// 結合セルを個別セルへ複製展開し、セル内のbrを区切り文字へ置換するスクリプト。
/// 列位置はrowspan/colspanを考慮した格子を組み立てて求める。
/// 内側のテーブルを先に処理しないと外側のセル複製で処理前の姿が固定されるため、逆順に走査する。
/// rowspan/colspanは外部HTML由来の未検証値で、HTML仕様上は65534と1000まで指定できる。
/// そのまま使うと悪意ある値や編集ミスで格子が数千万要素へ膨らむため、
/// rowspanは実際の残り行数で、colspanは現実のwikiテーブルを大きく超える定数で切り詰める。
/// 展開で増える位置はテキストだけを持つ複製にする。
/// サブツリーの深いコピーを繰り返すと入れ子テーブルを含むセルで乗算的に膨らむため。
let private flattenTablesScript =
    """() => {
    for (const br of document.querySelectorAll("th br, td br")) {
        // セル先頭のbr(画像除去の跡など)を区切り文字にすると空要素との区切りが残るため、
        // 前に中身のあるbrだけを区切り文字にして、それ以外は取り除く。
        let hasPrecedingContent = false;
        for (let sibling = br.previousSibling; sibling; sibling = sibling.previousSibling) {
            if (sibling.textContent.trim() !== "") {
                hasPrecedingContent = true;
                break;
            }
        }
        if (hasPrecedingContent) {
            br.replaceWith(" / ");
        } else {
            br.remove();
        }
    }
    const maxColumnSpan = 100;
    for (const table of Array.from(document.querySelectorAll("table")).reverse()) {
        const rows = Array.from(table.rows);
        const grid = [];
        rows.forEach((row, rowIndex) => {
            grid[rowIndex] ??= [];
            let columnIndex = 0;
            for (const cell of Array.from(row.cells)) {
                while (grid[rowIndex][columnIndex] !== undefined) {
                    columnIndex++;
                }
                const rowSpan = Math.min(cell.rowSpan, rows.length - rowIndex);
                const columnSpan = Math.min(cell.colSpan, maxColumnSpan);
                for (let i = 0; i < rowSpan; i++) {
                    for (let j = 0; j < columnSpan; j++) {
                        grid[rowIndex + i] ??= [];
                        grid[rowIndex + i][columnIndex + j] = {
                            cell,
                            isOrigin: i === 0 && j === 0,
                        };
                    }
                }
                columnIndex += columnSpan;
            }
        });
        rows.forEach((row, rowIndex) => {
            const cells = (grid[rowIndex] ?? [])
                .filter((entry) => entry !== undefined)
                .map((entry) => {
                    if (entry.isOrigin) {
                        entry.cell.removeAttribute("rowspan");
                        entry.cell.removeAttribute("colspan");
                        return entry.cell;
                    }
                    const copy = document.createElement(entry.cell.tagName);
                    copy.textContent = entry.cell.textContent;
                    return copy;
                });
            row.replaceChildren(...cells);
        });
    }
}"""

/// URLへ遷移し、queryに従って本文だけをHTML文字列として抜き出す。
/// RemoveSelectorsの除去とUnwrapLinks・FlattenTablesの変形を施した後に、
/// ContentSelectorsへ一致した要素のouterHTMLを連結して返す。
/// 一致する要素が無いセレクタは読み飛ばす。
/// wikiruの脚注のように存在しないことが正常な要素があるためで、
/// RemoveSelectorsの0件一致も同様に正常として扱う。
/// ただし全ContentSelectorsが1件も一致しなかった場合は、
/// サイト側の構造変更で抽出が全滅した可能性が高く、
/// 空のナレッジによる上書きを防ぐためContentNotFoundを送出する。
/// エラー条件はwithPageと同じ。
let fetchContentHtml (browser: IBrowser) (url: Uri) (query: ContentQuery) : Task<string> =
    withPage browser url (fun page ->
        task {
            for selector in query.RemoveSelectors do
                let! _ = page.EvalOnSelectorAllAsync(selector, removeElementsScript)
                ()

            if query.UnwrapLinks then
                let! _ = page.EvalOnSelectorAllAsync("a", unwrapElementsScript)
                ()

            if query.FlattenTables then
                let! _ = page.EvaluateAsync flattenTablesScript
                ()

            let contents = ResizeArray()

            for selector in query.ContentSelectors do
                let! htmls = page.EvalOnSelectorAllAsync<string array>(selector, outerHtmlScript)
                contents.AddRange htmls

            if contents.Count = 0 then
                raise (ContentNotFound(url = url, selectors = query.ContentSelectors))

            return String.concat "\n" contents
        })
