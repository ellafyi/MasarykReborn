module Masaryk.Core.Services.Leaderboard.Counting

open System.Threading.Tasks
open Discord
open Discord.Rest
open Discord.WebSocket
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

/// Messages people wrote in this guild; bots, webhooks and system messages don't count.
let counts (guildId: uint64) (message: IMessage) : bool =
    match message.Channel with
    | :? IGuildChannel as channel ->
        channel.GuildId = guildId
        && not message.Author.IsBot
        && (message.Type = MessageType.Default || message.Type = MessageType.Reply)
    | _ -> false

/// Threads and forum posts count toward their parent channel.
let channelOf (channel: IChannel) : uint64 =
    match channel with
    | :? SocketThreadChannel as thread -> thread.ParentChannel.Id
    | :? RestThreadChannel as thread -> thread.ParentChannelId
    | _ -> channel.Id

let subscribe (client: DiscordSocketClient) (guildId: uint64) (repository: LeaderboardRepository) : unit =
    let worker =
        Worker.start (fun (message: IMessage) -> $"Counting {message.Channel.Id}/{message.Id}") (fun message ->
            repository.Count(channelOf message.Channel, message.Author.Id, message.Timestamp.UtcDateTime))

    client.add_MessageReceived (fun message ->
        if counts guildId message then
            worker.Post message

        Task.CompletedTask)
