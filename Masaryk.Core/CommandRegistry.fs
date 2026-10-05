module Masaryk.Core.CommandRegistry

open Masaryk.Core

let create starboardCommand : Command list =
    [ Commands.Cummy.command; Commands.Test.command; starboardCommand ]

let byName commands =
    commands
    |> List.countBy (fun command -> command.Name)
    |> List.tryFind (fun (_, count) -> count > 1)
    |> Option.iter (fun (name, _) -> invalidOp $"Duplicate command name: {name}")

    commands |> List.map (fun command -> command.Name, command) |> Map.ofList
