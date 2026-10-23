module Masaryk.Core.Services.Leaderboard.Counting

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let subscribe (client: DiscordSocketClient) (guildId: uint64) (repository: LeaderboardRepository) : unit =
    let worker =
        Worker.start (fun (message: IMessage) -> $"Counting {message.Channel.Id}/{message.Id}") (fun message ->
            repository.Count(Messages.parentChannel message.Channel, message.Author.Id, message.Timestamp.UtcDateTime))

    client.add_MessageReceived (fun message ->
        if Messages.fromMember guildId message then
            worker.Post message

        Task.CompletedTask)
