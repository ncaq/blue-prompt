/// テスト用のローカルHTTPサーバ。
/// 外部サイトの構造やネットワークに依存せずにページ取得を検証するための足場。
module BluePrompt.Test.LocalServer

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks

/// ローカルHTTPサーバが受け取った1つのリクエスト。
type Request =
    {
        /// GETやPOSTのようなメソッド。
        Method: string
        /// リクエスト行のパス。クエリ文字列も含む。
        Target: string
        /// ヘッダ。名前は小文字に揃える。
        Headers: Map<string, string>
    }

    /// クエリ文字列を除いたパス。
    member this.Path = (this.Target.Split '?')[0]

/// ローカルHTTPサーバが返す1つの応答。
type Response =
    {
        /// HTTPステータス行の後半。例: "200 OK"
        Status: string
        /// Content-Typeヘッダの値。例: "text/html; charset=utf-8"
        ContentType: string
        /// 応答本文。文字コードの検証に使えるように生のバイト列で持つ。
        Body: byte array
        /// Locationなど追加で返すヘッダ行。
        ExtraHeaders: string list
    }

/// UTF-8のHTMLを返す普通の成功応答。
let htmlResponse (html: string) : Response =
    { Status = "200 OK"
      ContentType = "text/html; charset=utf-8"
      Body = Encoding.UTF8.GetBytes html
      ExtraHeaders = [] }

/// ヘッダの終わりを示す空行。
let private headerEnd = "\r\n\r\n"B

/// 読めた分の中でヘッダの終わりの空行が始まる位置。まだ無ければNone。
let private headerEndIndex (received: byte array) : int option =
    Seq.init (max 0 (received.Length - headerEnd.Length + 1)) id
    |> Seq.tryFind (fun index -> received.AsSpan(index, headerEnd.Length).SequenceEqual(headerEnd))

/// ヘッダの終わりの空行が現れるまで読み足して、読めた分を返す。
/// TCPはメッセージ境界を保証しないため1回の読み取りで揃うとは限らない。
/// 再帰関数をtaskの中へ置くとステートマシンを静的にコンパイルできなくなるため、モジュールの直下に置く。
let rec private readHead (stream: NetworkStream) (received: byte array) : Task<byte array * int> =
    task {
        match headerEndIndex received with
        | Some index -> return received, index
        | None ->
            let chunk: byte array = Array.zeroCreate 8192
            let! read = stream.ReadAsync(chunk.AsMemory())

            if read = 0 then
                failwith "ヘッダが完結する前に接続が閉じられました"

            return! readHead stream (Array.append received chunk[.. read - 1])
    }

/// リクエスト行とヘッダを読んでRequestにする。
let private parseHead (head: string) : Request =
    let lines = head.Split "\r\n"

    let method, target =
        match lines[0].Split ' ' with
        | parts when 2 <= parts.Length -> parts[0], parts[1]
        | _ -> "", "/"

    let headers =
        lines[1..]
        |> Array.choose (fun line ->
            match line.IndexOf ':' with
            | -1 -> None
            | index ->
                Some(
                    line.Substring(0, index).Trim().ToLowerInvariant(),
                    line.Substring(index + 1).Trim()
                ))
        |> Map.ofArray

    { Method = method
      Target = target
      Headers = headers }

/// リクエストを受け取って応答を返すローカルHTTPサーバを立てて、そのURLをactionへ渡す。
/// ポート0のListenerをそのまま使い、OSに空きポートを割り当てさせて他のテストとの衝突を避ける。
/// localhostがIPv4とIPv6のどちらへ解決されても繋がるように、両方を受けるソケットで待ち受ける。
///
/// 接続は1つずつ順に処理するため、respondの中で状態を持っても競合しない。
let withRequestServer (respond: Request -> Response) (action: Uri -> Task<'T>) : Task<'T> =
    task {
        use listener = TcpListener.Create 0
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port

        // リダイレクトの追跡などで1回の取得でも複数回リクエストが来るため、全リクエストへ応答し続ける。
        // listener.Stop()でAcceptが例外になりループごと終了する。
        let serving =
            task {
                while true do
                    use! client = listener.AcceptTcpClientAsync()

                    // サーバ側の処理が失敗した時にテストが原因不明のハングにならないように、
                    // 接続単位でtry-withで包み、失敗した接続は閉じてクライアント側のエラーとして観測させる。
                    try
                        let stream = client.GetStream()

                        let! received, index = readHead stream [||]
                        let request = parseHead (Encoding.ASCII.GetString(received, 0, index))

                        // ボディは見ないが、読み切らずに閉じると送信側で接続がリセットされ得る。
                        let bodyLength =
                            request.Headers
                            |> Map.tryFind "content-length"
                            |> Option.map int
                            |> Option.defaultValue 0

                        let alreadyRead = received.Length - index - headerEnd.Length
                        let remaining = bodyLength - alreadyRead

                        if 0 < remaining then
                            let rest: byte array = Array.zeroCreate remaining
                            do! stream.ReadExactlyAsync(rest.AsMemory())

                        let response = respond request

                        let header =
                            $"HTTP/1.1 %s{response.Status}\r\n"
                            + $"Content-Type: %s{response.ContentType}\r\n"
                            + $"Content-Length: %d{response.Body.Length}\r\n"
                            + String.concat
                                ""
                                (List.map (fun h -> h + "\r\n") response.ExtraHeaders)
                            + "Connection: close\r\n\r\n"

                        do! stream.WriteAsync((Encoding.ASCII.GetBytes header).AsMemory())
                        do! stream.WriteAsync(response.Body.AsMemory())
                    with error ->
                        // 応答せず接続だけ閉じることで、
                        // クライアント側では取得失敗のエラーとして現れる。
                        eprintfn $"LocalServer: 接続の処理に失敗しました: %O{error}"
            }

        try
            return! action (Uri $"http://localhost:%d{port}/")
        finally
            listener.Stop()
            ignore serving
    }

/// リクエストのパスに応じた応答を返すローカルHTTPサーバを立てて、そのURLをactionへ渡す。
/// respondが受け取るのはリクエスト行のパスで、クエリ文字列も含む。
let withServer (respond: string -> Response) (action: Uri -> Task<'T>) : Task<'T> =
    withRequestServer (fun request -> respond request.Target) action

/// 全リクエストへ同じUTF-8のHTMLを返すローカルHTTPサーバを立てて、そのURLをactionへ渡す。
let withServedHtml (html: string) (action: Uri -> Task<'T>) : Task<'T> =
    withServer (fun _ -> htmlResponse html) action
