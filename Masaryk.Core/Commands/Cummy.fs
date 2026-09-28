module Masaryk.Core.Commands.Cummy

open Masaryk.Core

let command: Command =
    { Name = "cummy"
      Description = "Cummy"
      Handle = fun interaction -> task { do! interaction.RespondAsync("Hello from Cummy") } }
