module Masaryk.Core.Services.Shared.Messages

open Discord
open Discord.Rest
open Discord.WebSocket

/// Messages people wrote in this guild; bots, webhooks and system messages don't count.
let fromMember (guildId: uint64) (message: IMessage) : bool =
    match message.Channel with
    | :? IGuildChannel as channel ->
        channel.GuildId = guildId
        && not message.Author.IsBot
        && (message.Type = MessageType.Default || message.Type = MessageType.Reply)
    | _ -> false

/// Threads and forum posts belong to their parent channel.
let parentChannel (channel: IChannel) : uint64 =
    match channel with
    | :? SocketThreadChannel as thread -> thread.ParentChannel.Id
    | :? RestThreadChannel as thread -> thread.ParentChannelId
    | _ -> channel.Id
