/// テストで立てるWireMockのサーバの共通の組み立て。
module BluePrompt.Test.MockServer

open System
open WireMock.RequestBuilders
open WireMock.ResponseBuilders
open WireMock.Server

/// どのメソッドとパスにも一致するリクエストの条件。
let anyRequest () : IRequestBuilder = Request.Create().UsingAnyMethod()

/// HTMLを返す応答。
let htmlResponse (html: string) : IResponseBuilder =
    Response.Create().WithHeader("Content-Type", "text/html; charset=utf-8").WithBody(html)

/// サーバのベースURL。
let baseUri (server: WireMockServer) : Uri = Uri server.Url

/// どのリクエストにも同じ応答を返すサーバを空きポートで立てる。
/// 呼び出し側が`use`で受けて止める。
let serve (response: IResponseBuilder) : WireMockServer =
    let server = WireMockServer.Start()
    server.Given(anyRequest ()).RespondWith(response) |> ignore
    server

/// どのリクエストにも同じHTMLを返すサーバを空きポートで立てる。
let serveHtml (html: string) : WireMockServer = serve (htmlResponse html)

/// サーバが受け取ったリクエストのパスを受け取った順に並べる。
let requestedPaths (server: WireMockServer) : string list =
    server.LogEntries |> Seq.map _.RequestMessage.Path |> List.ofSeq
