module Masaryk.Core.Services.Starboard.Feature

open Masaryk.Core
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let start: Feature =
    fun context ->
        task {
            let repository = StarboardRepository(context.Database)
            let configuration = Configuration.ConfigurationStore(repository, context.GuildId)
            let! loaded = configuration.Reload()
            printfn $"{loaded}"

            Worker.start
                (fun (reaction: DiscordAdapter.ReactionEvent) ->
                    $"Starboard processing for {reaction.ChannelId}/{reaction.MessageId}")
                (fun reaction -> Capture.handle context.Client repository context.GuildId configuration.Boards reaction)
            |> DiscordAdapter.subscribe context.Client context.GuildId

            return [ SlashCommand.command configuration ]
        }
