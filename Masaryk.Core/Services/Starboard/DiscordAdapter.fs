module Masaryk.Core.Services.Starboard.DiscordAdapter

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core.Services.Shared
open Masaryk.Core.Services.Starboard

type ReactionEvent =
    {
        ChannelId: uint64
        MessageId: uint64
        Emoji: EmojiKey
        Added: bool
    }

let emoji (emote: IEmote) : Emoji =
    {
        Key =
            match emote with
            | :? Emote as custom -> EmojiKey.Custom custom.Id
            | _ -> EmojiKey.Unicode emote.Name
        Text = emote.ToString()
    }

/// Every user who reacted, with normal and super reactions.
let readReactors (message: IMessage) (emote: IEmote) (metadata: ReactionMetadata) : Task<Reactor list> =
    task {
        let users = ResizeArray<Reactor>()

        for reactionType, count in
            [
                ReactionType.Normal, metadata.NormalCount
                ReactionType.Burst, metadata.BurstCount
            ] do
            if count > 0 then
                use pages =
                    message.GetReactionUsersAsync(emote, Int32.MaxValue, ``type`` = reactionType).GetAsyncEnumerator()

                let mutable reading = true

                while reading do
                    let! next = pages.MoveNextAsync()
                    reading <- next

                    if next then
                        for user in pages.Current do
                            users.Add({ UserId = user.Id; IsBot = user.IsBot })

        return List.ofSeq users
    }

/// The board and qualifying emoji for a message in this guild, if it qualifies.
let evaluate
    (client: DiscordSocketClient)
    (guildId: uint64)
    (boards: Board list)
    (reaction: ReactionEvent)
    : Task<(Board * IMessage * QualifyingReaction) option> =
    task {
        let! channel = client.Rest.GetChannelAsync(reaction.ChannelId)

        match channel :> IChannel with
        | :? IGuildChannel as source when source.GuildId = guildId ->
            match Evaluation.resolveBoard boards (Replies.parentOrSelf source) with
            | None -> return None
            | Some board ->
                // Fetch through REST; gateway caches can miss old messages or recent reactions.
                let! message = (source :?> IMessageChannel).GetMessageAsync(reaction.MessageId)

                if isNull message then
                    return None
                else
                    let observations = ResizeArray<ReactionObservation>()

                    for KeyValue(emote, metadata) in message.Reactions do
                        // Raw totals only decide which emoji could reach the threshold.
                        if metadata.ReactionCount >= board.Threshold then
                            let! reactors = readReactors message emote metadata

                            observations.Add(
                                {
                                    Emoji = emoji emote
                                    Reactors = reactors
                                }
                            )

                    return
                        Evaluation.qualifyingReaction board.Threshold message.Author.Id reaction.Emoji observations
                        |> Option.map (fun qualifying -> board, message, qualifying)
        | _ -> return None
    }

/// Queue reaction changes immediately so Discord's gateway never waits for REST calls.
let subscribe (client: DiscordSocketClient) (guildId: uint64) (reactions: MailboxProcessor<ReactionEvent>) : unit =
    let handler (added: bool) =
        Func<Cacheable<IUserMessage, uint64>, Cacheable<IMessageChannel, uint64>, SocketReaction, Task>
            (fun _ channel reaction ->
                let elsewhere =
                    channel.HasValue
                    && not (
                        channel.Value :? IGuildChannel
                        && (channel.Value :?> IGuildChannel).GuildId = guildId
                    )

                if not elsewhere then
                    reactions.Post(
                        {
                            ChannelId = channel.Id
                            MessageId = reaction.MessageId
                            Emoji = (emoji reaction.Emote).Key
                            Added = added
                        }
                    )

                Task.CompletedTask)

    client.add_ReactionAdded (handler true)
    client.add_ReactionRemoved (handler false)
