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
        /// imgをalt属性のテキストへ置き換えるか。
        /// 素材や品物を画像で表現しているページでは、altに入っている名前が唯一の事実になる。
        /// altが空の画像と、altがただのファイル名でしかない画像は取り除く。
        /// falseの場合は何もしないので、画像を消したい時はRemoveSelectorsに"img"を入れる。
        ReplaceImagesWithAlt: bool
        /// テーブルのrowspan/colspanを個別セルへ展開し、セル内の改行やブロック要素を1行へ繋ぐか。
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

/// imgをalt属性のテキストへ置き換えるスクリプト。
/// altが空の画像と、altがただのファイル名(画像拡張子で終わる)の画像は取り除く。
let private replaceImagesWithAltScript =
    """(elements) => elements.forEach((image) => {
    const alt = (image.getAttribute("alt") ?? "").trim();
    if (alt === "" || /\.(png|jpe?g|gif|webp|svg|avif)$/i.test(alt)) {
        image.remove();
    } else {
        image.replaceWith(alt);
    }
})"""

/// 一致した要素それぞれのouterHTMLを列挙するスクリプト。
let private outerHtmlScript =
    "(elements) => elements.map((element) => element.outerHTML)"

/// テーブルをGFMのパイプテーブルで表現できて、データとして読める形へ変形するスクリプト。
/// 表全体を横断する1セルだけの行(小見出しや自由記述)は段落として表の外へ出し、
/// 結合セルを個別セルへ複製展開し、セル内の改行やブロック要素を1行へ繋ぐ。
/// さらに空になった行と列を取り除き、
/// キーと値のペアを横に繰り返すレイアウトの行を1行1ペアへ正規化する。
/// GFMのパイプテーブルはセル内の改行を表現できないため、
/// 境界に句読点や区切り記号が既にあれば詰めて、無ければ読点で繋ぐ。
/// 対象が日本語のwikiであることを前提にした結合規則。
/// 列位置はrowspan/colspanを考慮した格子を組み立てて求める。
/// 内側のテーブルを先に処理しないと外側のセル複製で処理前の姿が固定されるため、逆順に走査する。
/// rowspan/colspanは外部HTML由来の未検証値で、HTML仕様上は65534と1000まで指定できる。
/// そのまま使うと悪意ある値や編集ミスで格子が数千万要素へ膨らむため、
/// rowspanは実際の残り行数で、colspanは現実のwikiテーブルを大きく超える定数で切り詰める。
/// 縦方向の展開で増える位置は行の見出しとして意味があるためテキストだけを持つ複製にし、
/// 横方向の展開で増える位置は同じ行を読めば分かる繰り返しでしかないため空にする。
/// サブツリーの深いコピーを繰り返すと入れ子テーブルを含むセルで乗算的に膨らむため、
/// どちらもサブツリーはコピーしない。
let private flattenTablesScript =
    """() => {
    // 表全体を横断する1セルだけの行は、表の中の小見出しや自由記述であり、
    // 列を持つ行と同じ表に混ぜると、長い記述に合わせた列幅の整形で他の行が際限なく伸びる。
    // 段落として表の外へ出し、表をその前後で分割する。
    // 全行が1セルの表は1列の表として意味を持つのでそのまま残す。
    // 見出しを含む表はデータの表ではなくレイアウトなので触らない。
    for (const table of Array.from(document.querySelectorAll("table"))) {
        if (table.querySelector("h1, h2, h3, h4, h5, h6")) {
            continue;
        }
        const rows = Array.from(table.rows);
        // 1セルだけに見える行には上からのrowspanで埋まる継続行もあるため、
        // colspanで横に広がっている行だけを区切り行とみなす。
        const isDivider = (row) => row.cells.length === 1 && row.cells[0].colSpan > 1;
        if (!rows.some(isDivider) || rows.every(isDivider)) {
            continue;
        }
        const fragments = [];
        let pendingRows = [];
        const flushRows = () => {
            if (pendingRows.length === 0) {
                return;
            }
            const segment = document.createElement("table");
            segment.append(...pendingRows);
            fragments.push(segment);
            pendingRows = [];
        };
        for (const row of rows) {
            if (isDivider(row)) {
                flushRows();
                const cell = row.cells[0];
                const paragraph = document.createElement("p");
                if (cell.tagName === "TH") {
                    // 見出しセルだった行は小見出しなので、強調で見出しらしさを残す。
                    const emphasis = document.createElement("strong");
                    emphasis.append(...cell.childNodes);
                    paragraph.append(emphasis);
                } else {
                    paragraph.append(...cell.childNodes);
                }
                fragments.push(paragraph);
            } else {
                pendingRows.push(row);
            }
        }
        flushRows();
        table.replaceWith(...fragments);
    }
    // 改行の境界をどう繋ぐか決める。
    // 前が句読点や開き括弧などで終わるか、後が区切り記号や閉じ括弧などで始まるなら、
    // そのまま詰めても読める。どちらでもなければ読点を挟む。
    const joinSeparator = (previousText, nextText) => {
        const previousChar = previousText.slice(-1);
        const nextChar = nextText.charAt(0);
        const flushBefore = "。．！？!?…、,/／・:：(（「『";
        const flushAfter = "/／・、。,)）」』(（";
        return flushBefore.includes(previousChar) || flushAfter.includes(nextChar) ? "" : "、";
    };
    // 兄弟を遡って最初に中身のあるテキストを返す。
    const precedingText = (node) => {
        for (let sibling = node.previousSibling; sibling; sibling = sibling.previousSibling) {
            const text = sibling.textContent.trim();
            if (text !== "") {
                return text;
            }
        }
        return "";
    };
    const followingText = (node) => {
        for (let sibling = node.nextSibling; sibling; sibling = sibling.nextSibling) {
            const text = sibling.textContent.trim();
            if (text !== "") {
                return text;
            }
        }
        return "";
    };
    for (const br of document.querySelectorAll("th br, td br")) {
        // セル先頭や末尾のbr(画像除去の跡など)は繋ぐ相手がいないので取り除く。
        const previous = precedingText(br);
        const next = followingText(br);
        if (previous === "" || next === "") {
            br.remove();
        } else {
            br.replaceWith(joinSeparator(previous, next));
        }
    }
    // セル内のdivやpなどのブロック要素が残っていると、
    // pandocがパイプテーブルで表現できずテーブル全体を[TABLE]へ潰してしまう。
    // brと同じ要領で境界を繋いでタグを外す。
    // querySelectorAllは文書順(親が先)なので、逆順に走査して内側から外す。
    // ただしページ全体をテーブルで組むレイアウトでは、本文のあらゆるブロックがセルの子孫になる。
    // 見出しやテーブルなどの構造を含むブロックと、
    // 見出しを含むテーブル(データの表ではなくレイアウト)の配下は外さない。
    const cellBlocks = Array.from(document.querySelectorAll("th div, th p, td div, td p"));
    for (const block of cellBlocks.reverse()) {
        if (block.querySelector("h1, h2, h3, h4, h5, h6, table, ul, ol")) {
            continue;
        }
        const enclosingTable = block.closest("table");
        if (enclosingTable && enclosingTable.querySelector("h1, h2, h3, h4, h5, h6")) {
            continue;
        }
        const text = block.textContent.trim();
        const previous = precedingText(block);
        if (previous !== "" && text !== "") {
            const separator = joinSeparator(previous, text);
            if (separator !== "") {
                block.before(separator);
            }
        }
        block.replaceWith(...block.childNodes);
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
                            // 縦方向(rowspan由来)の複製だけがテキストを引き継ぐ。
                            keepsText: j === 0,
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
                    copy.textContent = entry.keepsText ? entry.cell.textContent : "";
                    return copy;
                });
            // 画像の除去などで全セルが空になった行はノイズでしかない。
            if (cells.every((cell) => cell.textContent.trim() === "")) {
                row.remove();
            } else {
                row.replaceChildren(...cells);
            }
        });
        // 画像の除去などで見出しの下が全て空になった列は、列ごとノイズでしかない。
        // ただし2行の表では空セルがデータの欠けを意味しうるので、
        // 見出しに中身のある列は3行以上の表でだけ取り除く。
        const survivingRows = Array.from(table.rows);
        if (2 <= survivingRows.length) {
            const columnCount = Math.max(...survivingRows.map((row) => row.cells.length));
            for (let index = columnCount - 1; 0 <= index; index--) {
                const header = survivingRows[0].cells[index];
                const headerIsEmpty = !header || header.textContent.trim() === "";
                const bodyIsEmpty = survivingRows.every(
                    (row, rowIndex) =>
                        rowIndex === 0 ||
                        !row.cells[index] ||
                        row.cells[index].textContent.trim() === "",
                );
                if (bodyIsEmpty && (headerIsEmpty || 3 <= survivingRows.length)) {
                    for (const row of survivingRows) {
                        row.cells[index]?.remove();
                    }
                }
            }
        }
        // キーと値のペアを横に並べたレイアウト(th,td,th,td,...)は、
        // 列の意味が揃わずデータとして混乱するため、1行1ペアの2列へ正規化する。
        // 行末尾の空セルは幅合わせの埋め草なので、ペアの判定から除いて捨てる。
        // 中身が両方空のペアも行にしない。
        for (const row of Array.from(table.rows)) {
            const cells = Array.from(row.cells);
            let length = cells.length;
            while (0 < length && cells[length - 1].textContent.trim() === "") {
                length--;
            }
            const pairCells = cells.slice(0, length);
            const isPairsRow =
                4 <= pairCells.length &&
                pairCells.length % 2 === 0 &&
                pairCells.every((cell, index) => cell.tagName === (index % 2 === 0 ? "TH" : "TD"));
            if (!isPairsRow) {
                continue;
            }
            const pairRows = [];
            for (let index = 0; index < pairCells.length; index += 2) {
                const key = pairCells[index];
                const value = pairCells[index + 1];
                if (key.textContent.trim() === "" && value.textContent.trim() === "") {
                    continue;
                }
                const pairRow = document.createElement("tr");
                pairRow.append(key, value);
                pairRows.push(pairRow);
            }
            row.replaceWith(...pairRows);
        }
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

            if query.ReplaceImagesWithAlt then
                let! _ = page.EvalOnSelectorAllAsync("img", replaceImagesWithAltScript)
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
