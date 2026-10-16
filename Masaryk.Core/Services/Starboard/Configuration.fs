module Masaryk.Core.Services.Starboard.Configuration

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Masaryk.Core.Services.Starboard
open Masaryk.Postgres

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

/// Each message must belong to at most one board.
let private validate (boards: Board list) : unit =
    require (boards |> List.countBy _.Name |> List.forall (fun (_, count) -> count = 1)) "Board names must be unique."

    require
        (boards |> List.countBy _.Target |> List.forall (fun (_, count) -> count = 1))
        "Each board must have a different target channel."

    let targets = boards |> List.map _.Target |> Set.ofList
    let sources (board: Board) = Set.difference board.Channels targets

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

let parse (json: string) : Board list =
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

            {
                Name = board.Name
                Mode =
                    match board.Mode with
                    | "whitelist" -> SourceMode.Whitelist
                    | "blacklist" -> SourceMode.Blacklist
                    | _ ->
                        raise (InvalidConfiguration $"Board '{board.Name}' mode must be 'whitelist' or 'blacklist'.")
                Channels = Set.ofArray board.Channels
                Target = board.Target
                Threshold = board.Threshold
                IsActive = board.Active.GetValueOrDefault(true)
            })
        |> Array.toList

    validate boards
    boards

/// The boards in use; a failed reload keeps the previous ones.
type ConfigurationStore(repository: StarboardRepository, guildId: uint64) =
    let mutable boards: Board list = []

    member _.Boards = boards

    member _.Reload() : Task<string> =
        task {
            let! json = repository.GetConfiguration(guildId)
            boards <- parse json
            return $"Starboard configuration loaded: {boards.Length} boards."
        }
