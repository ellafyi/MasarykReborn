module Masaryk.Core.Services.Leaderboard.SlashCommand

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Core.Services.Leaderboard
open Masaryk.Postgres

let private cooldown = TimeSpan.FromSeconds 10.0

let private periodOption () =
    let period =
        Options.option "period" "Time period (default: all time)" ApplicationCommandOptionType.String

    for value, name in Ranking.periods do
        period.AddChoice(name, value) |> ignore

    period

let command (repository: LeaderboardRepository) : Command =
    let lastUse = ConcurrentDictionary<uint64, DateTimeOffset>()

    let handle (interaction: SocketSlashCommand) : Task =
        task {
            let now = DateTimeOffset.UtcNow

            match lastUse.TryGetValue interaction.User.Id with
            | true, last when now - last < cooldown ->
                let again =
                    TimestampTag.FromDateTimeOffset(last + cooldown, TimestampTagStyles.Relative)

                do! interaction.RespondAsync($"You can use this again {again}.", ephemeral = true)
            | _ ->
                lastUse[interaction.User.Id] <- now
                do! interaction.DeferAsync()
                let subcommand, option = Options.input interaction
                let period = option "period" |> Option.map string |> Option.defaultValue "all"

                let! embed =
                    if subcommand = "channels" then
                        task {
                            let! rows = repository.Channels period
                            return Ranking.channels period rows
                        }
                    else
                        let memberId =
                            option "member"
                            |> Option.map (fun user -> (user :?> IUser).Id)
                            |> Option.defaultValue interaction.User.Id

                        let channelId =
                            option "channel" |> Option.map (fun channel -> (channel :?> IChannel).Id)

                        task {
                            let! rows = repository.Users(period, channelId, memberId)
                            return Ranking.users period channelId memberId rows
                        }

                do! interaction.FollowupAsync(embed = embed, allowedMentions = AllowedMentions.None) :> Task
        }

    let channel =
        Options.option
            "channel"
            "Only count messages in this channel, including its threads"
            ApplicationCommandOptionType.Channel

    for kind in
        [
            ChannelType.Text
            ChannelType.News
            ChannelType.Forum
            ChannelType.Voice
            ChannelType.Stage
        ] do
        channel.AddChannelType(kind) |> ignore

    {
        Name = "leaderboard"
        Description = "Who sends the most messages"
        Options =
            [
                Options.subcommand
                    "users"
                    "Top members by messages sent"
                    [
                        Options.option
                            "member"
                            "Whose position to show (default: you)"
                            ApplicationCommandOptionType.User
                        channel
                        periodOption ()
                    ]
                Options.subcommand "channels" "Most active channels" [ periodOption () ]
            ]
        Permission = None
        Handle = handle
    }
