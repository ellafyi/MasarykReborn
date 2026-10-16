module Masaryk.Core.CommandRegistry

open Masaryk.Core

/// Commands that don't belong to a feature.
let builtIn: Command list = [ Commands.Cummy.command; Commands.Test.command ]

let byName (commands: Command list) : Map<string, Command> =
    commands
    |> List.countBy (fun command -> command.Name)
    |> List.tryFind (fun (_, count) -> count > 1)
    |> Option.iter (fun (name, _) -> invalidOp $"Duplicate command name: {name}")

    commands |> List.map (fun command -> command.Name, command) |> Map.ofList
