module Masaryk.Core.Starboard.Runtime

open System
open System.Threading
open System.Threading.Tasks
open Masaryk.Core.Starboard
open Masaryk.Postgres

type ConfigurationStore(repository: StarboardRepository, guildId: uint64) =
    let reload = new SemaphoreSlim(1, 1)
    let mutable boards = []

    member _.Boards: Board list = Volatile.Read(&boards)

    member _.Reload() : Task<string> =
        task {
            do! reload.WaitAsync()

            try
                let! json = repository.GetConfiguration(guildId)
                let settings = Configuration.parse json

                let definitions: Masaryk.Postgres.Starboard.BoardConfiguration list =
                    settings
                    |> List.map (fun board ->
                        {
                            Name = board.Name
                            Target = board.Target
                            Threshold = board.Threshold
                            IsActive = board.IsActive
                        })

                let! ids = repository.SynchronizeBoards(guildId, definitions)

                let updated =
                    settings
                    |> List.map (fun settings ->
                        {
                            Id = ids[settings.Name]
                            Settings = settings
                        })

                let previous =
                    boards
                    |> List.map (fun board -> board.Settings.Name, board.Settings)
                    |> Map.ofList

                let current = settings |> List.map (fun board -> board.Name, board) |> Map.ofList

                let added =
                    current
                    |> Map.filter (fun name _ -> not (Map.containsKey name previous))
                    |> Map.count

                let removed =
                    previous
                    |> Map.filter (fun name _ -> not (Map.containsKey name current))
                    |> Map.count

                let changed =
                    current
                    |> Map.filter (fun name settings -> previous |> Map.tryFind name |> Option.exists ((<>) settings))
                    |> Map.count

                Volatile.Write(&boards, updated)

                return
                    if added + removed + changed = 0 then
                        "Starboard configuration unchanged."
                    else
                        $"Starboard configuration reloaded: {added} added, {changed} changed, {removed} removed."
            finally
                reload.Release() |> ignore
        }

    interface IDisposable with
        member _.Dispose() : unit = reload.Dispose()
