module Masaryk.Core.Services.Leaderboard.Feature

open Masaryk.Core
open Masaryk.Postgres

let start: Feature =
    fun context ->
        task {
            let repository = LeaderboardRepository(context.Database)
            let! since = repository.StartCounting()
            printfn $"Leaderboard: counting messages since {since:u}."
            Counting.subscribe context.Client context.GuildId repository
            return [ SlashCommand.command repository ]
        }
