module Masaryk.Core.Services.Shared.Cooldown

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Discord
open Discord.WebSocket

/// Run `handle` at most once per `period` for each user; earlier tries get a private reply.
let limit (period: TimeSpan) (handle: SocketSlashCommand -> Task) : SocketSlashCommand -> Task =
    let lastUse = ConcurrentDictionary<uint64, DateTimeOffset>()

    fun interaction ->
        task {
            let now = DateTimeOffset.UtcNow

            match lastUse.TryGetValue interaction.User.Id with
            | true, last when now - last < period ->
                let again =
                    TimestampTag.FromDateTimeOffset(last + period, TimestampTagStyles.Relative)

                do! interaction.RespondAsync($"You can use this again {again}.", ephemeral = true)
            | _ ->
                lastUse[interaction.User.Id] <- now
                do! handle interaction
        }
