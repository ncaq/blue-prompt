/// テスト用のローカルHTTPサーバ。
/// 外部サイトの構造やネットワークに依存せずにページ取得を検証するための足場。
module BluePrompt.Test.LocalServer

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks

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

/// リクエストのパスに応じた応答を返すローカルHTTPサーバを立てて、そのURLをactionへ渡す。
/// ポート0でOSに空きポートを割り当てさせて他のテストとの衝突を避ける。
let withServer (respond: string -> Response) (action: Uri -> Task<'T>) : Task<'T> =
    task {
        use listener = new TcpListener(IPAddress.Loopback, 0)
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

                        // TCPはメッセージ境界を保証しないため、
                        // リクエスト行の終端(\r\n)が現れるまで読み足す。
                        let chunk: byte array = Array.zeroCreate 8192
                        let mutable request = ""

                        while not (request.Contains "\r\n") do
                            let! read = stream.ReadAsync(chunk.AsMemory())

                            if read = 0 then
                                failwith "リクエスト行が完結する前に接続が閉じられました"

                            request <- request + Encoding.ASCII.GetString(chunk, 0, read)

                        // リクエスト行「GET /path HTTP/1.1」からパスを取り出す。
                        let path =
                            match request.Split ' ' with
                            | parts when 2 <= parts.Length -> parts[1]
                            | _ -> "/"

                        let response = respond path

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
            return! action (Uri $"http://127.0.0.1:%d{port}/")
        finally
            listener.Stop()
            ignore serving
    }

/// 全リクエストへ同じUTF-8のHTMLを返すローカルHTTPサーバを立てて、そのURLをactionへ渡す。
let withServedHtml (html: string) (action: Uri -> Task<'T>) : Task<'T> =
    withServer (fun _ -> htmlResponse html) action
