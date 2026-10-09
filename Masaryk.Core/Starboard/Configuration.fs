module Masaryk.Core.Starboard.Configuration

open System
open System.Text.Json
open System.Text.Json.Serialization
open Masaryk.Core.Starboard

exception InvalidConfiguration of string

[<CLIMutable>]
type JsonBoard =
    {
        Name: string
        Mode: string
        Channels: uint64 array
        Target: uint64
        Threshold: int
        Active: Nullable<bool>
    }

let private require (condition: bool) (message: string) : unit =
    if not condition then
        raise (InvalidConfiguration message)

let private validate (boards: BoardSettings list) : unit =
    require (boards |> List.countBy _.Name |> List.forall (fun (_, count) -> count = 1)) "Board names must be unique."

    require
        (boards |> List.countBy _.Target |> List.forall (fun (_, count) -> count = 1))
        "Each board must have a different target channel."

    let targets = boards |> List.map _.Target |> Set.ofList
    let sources (board: BoardSettings) : Set<uint64> = Set.difference board.Channels targets

    for index, left in List.indexed boards do
        for right in List.skip (index + 1) boards do
            let overlap =
                match left.Mode, right.Mode with
                | SourceMode.Whitelist, SourceMode.Whitelist ->
                    Set.intersect (sources left) (sources right) |> Set.isEmpty |> not
                | SourceMode.Whitelist, SourceMode.Blacklist ->
                    Set.difference (sources left) right.Channels |> Set.isEmpty |> not
                | SourceMode.Blacklist, SourceMode.Whitelist ->
                    Set.difference (sources right) left.Channels |> Set.isEmpty |> not
                | SourceMode.Blacklist, SourceMode.Blacklist -> true

            require (not overlap) $"Source rules overlap between '{left.Name}' and '{right.Name}'."

let parse (json: string) : BoardSettings list =
    let options =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        )

    let input =
        try
            JsonSerializer.Deserialize<JsonBoard array>(json, options)
        with :? JsonException as error ->
            raise (InvalidConfiguration error.Message)

    require (not (isNull input)) "Configuration must be a JSON array."

    let boards =
        input
        |> Array.map (fun board ->
            require (not (isNull (box board))) "Each array item must be a starboard object."
            require (not (String.IsNullOrWhiteSpace board.Name)) "Each board needs a name."
            require (board.Target > 0UL) $"Board '{board.Name}' needs a positive target channel ID."
            require (board.Threshold > 0) $"Board '{board.Name}' needs a positive threshold."
            require (not (isNull board.Channels)) $"Board '{board.Name}' needs a channels array."

            require
                (board.Channels |> Array.forall (fun id -> id > 0UL))
                $"Board '{board.Name}' has an invalid source channel ID."

            let mode =
                match board.Mode with
                | "whitelist" -> SourceMode.Whitelist
                | "blacklist" -> SourceMode.Blacklist
                | _ -> raise (InvalidConfiguration $"Board '{board.Name}' mode must be 'whitelist' or 'blacklist'.")

            {
                Name = board.Name
                Mode = mode
                Channels = Set.ofArray board.Channels
                Target = board.Target
                Threshold = board.Threshold
                IsActive = board.Active.GetValueOrDefault(true)
            })
        |> Array.toList

    validate boards
    boards

let parseManagerRoles (value: string) : Set<uint64> =
    if String.IsNullOrWhiteSpace value then
        Set.empty
    else
        value.Split(',', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun value ->
            match UInt64.TryParse value with
            | true, id when id > 0UL -> id
            | _ -> invalidOp "STARBOARD_MANAGER_ROLE_IDS must contain comma-separated positive role IDs.")
        |> Set.ofArray

let managerRolesFromEnvironment () : Set<uint64> =
    Environment.GetEnvironmentVariable("STARBOARD_MANAGER_ROLE_IDS")
    |> parseManagerRoles
