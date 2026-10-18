module Masaryk.Core.Services.Starboard.Feature

open Masaryk.Core
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let start: Feature =
    fun context ->
        task {
            let repository = StarboardRepository(context.Database)
            printfn $"Starboard: {context.Configuration.Current.Boards.Length} boards."

            Worker.start
                (fun (reaction: DiscordAdapter.ReactionEvent) ->
                    $"Starboard processing for {reaction.ChannelId}/{reaction.MessageId}")
                (fun reaction ->
                    Capture.handle
                        context.Client
                        repository
                        context.GuildId
                        context.Configuration.Current.Boards
                        reaction)
            |> DiscordAdapter.subscribe context.Client context.GuildId

            return [ SlashCommand.command context repository ]
        }
