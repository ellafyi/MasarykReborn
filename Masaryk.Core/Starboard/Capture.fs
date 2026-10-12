module Masaryk.Core.Starboard.Capture

open System.Threading.Tasks
open Discord.WebSocket
open Masaryk.Core.Starboard
open Masaryk.Postgres

/// Skip existing captures and save the first qualifying snapshot.
let handle
    (repository: StarboardRepository)
    (client: DiscordSocketClient)
    (guildId: uint64)
    (boards: Board list)
    (reaction: Events.ReactionEvent)
    : Task<unit> =
    task {
        let! captured = repository.HasEntry(reaction.MessageId)

        if not captured then
            let! result = DiscordAdapter.evaluate client guildId boards reaction

            match result with
            | None -> ()
            | Some result ->
                DiscordAdapter.report result

                match result.Qualification with
                | None -> ()
                | Some qualification ->
                    let snapshot = Snapshot.create result.Board.Id result.Message qualification
                    let! entryId = repository.SaveSnapshot(snapshot)

                    match entryId with
                    | Some id ->
                        printfn
                            $"Starboard {result.Board.Settings.Name}: captured entry {id} for {snapshot.SourceJumpUrl}."

                        let! entry = repository.FindEntry(snapshot.SourceMessageId)
                        do! Publication.publish repository client result.Board.Settings.Target (Option.get entry)
                    | None -> ()
    }
