module Masaryk.Core.CommandRegistry

open Masaryk.Core

let create (starboardCommand: Command) (leaderboardCommand: Command) : Command list =
    [
        Commands.Cummy.command
        Commands.Test.command
        starboardCommand
        leaderboardCommand
    ]

let byName (commands: Command list) : Map<string, Command> =
    commands
    |> List.countBy (fun command -> command.Name)
    |> List.tryFind (fun (_, count) -> count > 1)
    |> Option.iter (fun (name, _) -> invalidOp $"Duplicate command name: {name}")

    commands |> List.map (fun command -> command.Name, command) |> Map.ofList
