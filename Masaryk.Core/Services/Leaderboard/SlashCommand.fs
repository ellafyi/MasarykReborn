module Masaryk.Core.Services.Leaderboard.SlashCommand

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let private reply (repository: LeaderboardRepository) (interaction: SocketSlashCommand) : Task =
    task {
        do! interaction.DeferAsync()
        let subcommand, value = Options.input interaction
        let period = Period.read value

        let send (embed: Embed) =
            interaction.FollowupAsync(embed = embed, allowedMentions = AllowedMentions.None) :> Task

        match subcommand with
        | "channels" ->
            let! rows = repository.Channels(period, Ranking.top)
            do! send (Ranking.channels period rows)
        | _ ->
            let memberId =
                value "member"
                |> Option.map (fun user -> (user :?> IUser).Id)
                |> Option.defaultValue interaction.User.Id

            let channelId =
                value "channel" |> Option.map (fun channel -> (channel :?> IChannel).Id)

            let! rows =
                repository.Users(period, channelId, memberId, Ranking.top, Ranking.around)

            do! send (Ranking.users period channelId memberId rows)
    }

let command (repository: LeaderboardRepository) : Command =
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
                        Options.channelFilter "Only count messages in this channel, including its threads"
                        Period.option ()
                    ]
                Options.subcommand "channels" "Most active channels" [ Period.option () ]
            ]
        Permission = None
        Handle = Cooldown.limit (TimeSpan.FromSeconds 10.0) (reply repository)
    }
