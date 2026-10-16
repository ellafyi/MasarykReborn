module Masaryk.Core.Commands.Test

open Masaryk.Core

let command: Command =
    {
        Name = "test"
        Description = "testing"
        Options = []
        Permission = None
        Handle = fun interaction -> task { do! interaction.RespondAsync("testing") }
    }
