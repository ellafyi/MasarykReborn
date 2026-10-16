module Masaryk.Core.Services.Shared.Replies

open System.Threading.Tasks
open Discord
open Discord.Rest
open Discord.WebSocket

/// Threads and forum posts belong to their parent channel.
let parentOrSelf (channel: IChannel) : uint64 =
    match channel with
    | :? SocketThreadChannel as thread -> thread.ParentChannel.Id
    | :? RestThreadChannel as thread -> thread.ParentChannelId
    | _ -> channel.Id

let parentId (message: IMessage) : uint64 option =
    if message.Type = MessageType.Reply then
        Some message.Reference.MessageId.Value
    else
        None

/// Up to limit messages above this one in its reply chain, oldest first. A deleted parent ends the chain.
let rec ancestors (fetch: uint64 -> Task<IMessage>) (limit: int) (message: IMessage) : Task<IMessage list> =
    task {
        match parentId message with
        | Some id when limit > 0 ->
            let! parent = fetch id

            if isNull parent then
                return []
            else
                let! older = ancestors fetch (limit - 1) parent
                return older @ [ parent ]
        | _ -> return []
    }
