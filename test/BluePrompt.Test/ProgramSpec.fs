module BluePrompt.Test.ProgramSpec

open Xunit

[<Fact>]
let ``mainは0を返す`` () =
    Assert.Equal(0, BluePrompt.Program.main [||])
