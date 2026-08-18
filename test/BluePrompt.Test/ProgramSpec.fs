module BluePrompt.Test.ProgramSpec

open Xunit

[<Fact>]
let ``引数なしでは使い方を表示して0を返す`` () =
    Assert.Equal(0, BluePrompt.Program.main [||])

[<Fact>]
let ``未知のサブコマンドでは非0を返す`` () =
    Assert.Equal(1, BluePrompt.Program.main [| "unknown-subcommand" |])
