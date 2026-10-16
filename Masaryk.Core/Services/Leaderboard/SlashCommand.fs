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
    let option =
        SlashCommandOptionBuilder()
            .WithName("period")
            .WithDescription("Time period (default: all time)")
            .WithType(ApplicationCommandOptionType.String)

    for value, name in Ranking.periods do
        option.AddChoice(name, value) |> ignore

    option

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
                let subcommand = Seq.head interaction.Data.Options

                let option name =
                    subcommand.Options |> Seq.tryFind (fun o -> o.Name = name) |> Option.map _.Value

                let period = option "period" |> Option.map string |> Option.defaultValue "all"

                let! embed =
                    if subcommand.Name = "channels" then
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

                let! _ =
                    interaction.FollowupAsync(embed = embed, allowedMentions = AllowedMentions.None)

                ()
        }

    let channel =
        SlashCommandOptionBuilder()
            .WithName("channel")
            .WithDescription("Only count messages in this channel, including its threads")
            .WithType(ApplicationCommandOptionType.Channel)

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
                SlashCommandOptionBuilder()
                    .WithName("users")
                    .WithDescription("Top members by messages sent")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        SlashCommandOptionBuilder()
                            .WithName("member")
                            .WithDescription("Whose position to show (default: you)")
                            .WithType(ApplicationCommandOptionType.User)
                    )
                    .AddOption(channel)
                    .AddOption(periodOption ())
                SlashCommandOptionBuilder()
                    .WithName("channels")
                    .WithDescription("Most active channels")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(periodOption ())
            ]
        Permission = None
        Handle = handle
    }
