module Masaryk.Core.Commands.Cummy

open Masaryk.Core

let command: Command =
    {
        Name = "cummy"
        Description = "Cummy"
        Options = []
        Permission = None
        Handle = fun interaction -> task { do! interaction.RespondAsync("Hello from Cummy") }
    }
