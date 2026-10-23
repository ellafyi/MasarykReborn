module Masaryk.Core.Services.Starboard.Feature

open Masaryk.Core
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let start: Feature =
    fun context ->
        task {
            let repository = StarboardRepository(context.Database)

            Worker.start
                (fun (reaction: DiscordAdapter.ReactionEvent) ->
                    $"Starboard processing for {reaction.ChannelId}/{reaction.MessageId}")
                (Capture.handle context repository)
            |> DiscordAdapter.subscribe context.Client context.GuildId

            return [ SlashCommand.command context repository ]
        }
