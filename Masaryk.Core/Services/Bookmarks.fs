/// React with :bookmark: to get a copy of a message in your DMs; react with :wastebasket: on it to delete it.
module Masaryk.Core.Services.Bookmarks

open System
open System.Threading.Tasks
open Discord
open Discord.Net
open Discord.WebSocket
open Masaryk.Core.Services.Shared
open Masaryk.Core

/// :bookmark: from Discord's default emoji set.
let private saveEmoji = set [ "🔖" ]

/// :wastebasket:. Discord may send it with or without the emoji presentation selector.
let private deleteEmoji = set [ "🗑️"; "🗑" ]

/// Identifies the bot's bookmark DMs, so deleting never touches other DMs from the bot.
let private marker = "Bookmark · React with 🗑️ to delete"

[<RequireQualifiedAccess>]
type Action =
    | Save
    | Delete
    | Ignore

type Request =
    {
        ChannelId: uint64
        MessageId: uint64
        UserId: uint64
        Emote: IEmote
    }

/// Custom server emotes named "bookmark" don't count; only the default emoji does.
let private isEmoji (names: Set<string>) (emote: IEmote) : bool =
    match emote with
    | :? Emoji as emoji -> names.Contains emoji.Name
    | _ -> false

/// Save from this guild's channels and threads; delete only inside DMs.
let action (emote: IEmote) (channel: IChannel) (guildId: uint64) : Action =
    match channel with
    | :? IGuildChannel as guildChannel when guildChannel.GuildId = guildId && isEmoji saveEmoji emote -> Action.Save
    | :? IDMChannel when isEmoji deleteEmoji emote -> Action.Delete
    | _ -> Action.Ignore

let isBookmark (botId: uint64) (message: IMessage) : bool =
    message.Author.Id = botId
    && message.Embeds
       |> Seq.exists (fun embed ->
           embed.Footer.HasValue
           && embed.Footer.Value.Text.StartsWith(marker, StringComparison.Ordinal))

let render (message: IMessage) : Embed * MessageComponent =
    let embed =
        (MessageEmbed.build marker (MessageEmbed.fromMessage message))
            .AddField("Saved from", $"<#{message.Channel.Id}>")
            .Build()

    embed, MessageEmbed.jumpButton message

let private save (client: DiscordSocketClient) (channel: IMessageChannel) (request: Request) : Task<unit> =
    task {
        let! message = channel.GetMessageAsync(request.MessageId)

        if isNull message then
            eprintfn $"Bookmark source {request.ChannelId}/{request.MessageId} no longer exists."
        else
            let! user = client.Rest.GetUserAsync(request.UserId)
            let! dm = user.CreateDMChannelAsync()
            let embed, components = render message

            try
                let! _ =
                    dm.SendMessageAsync(embed = embed, components = components, allowedMentions = AllowedMentions.None)

                printfn $"Bookmark: sent {request.ChannelId}/{request.MessageId} to user {request.UserId}."
            with :? HttpException as error when error.DiscordCode = Nullable DiscordErrorCode.CannotSendMessageToUser ->
                printfn $"Bookmark: user {request.UserId} does not accept DMs."
    }

let private delete (client: DiscordSocketClient) (channel: IMessageChannel) (request: Request) : Task<unit> =
    task {
        let! message = channel.GetMessageAsync(request.MessageId)

        if not (isNull message) && isBookmark client.CurrentUser.Id message then
            do! message.DeleteAsync()
    }

let handle (client: DiscordSocketClient) (guildId: uint64) (request: Request) : Task<unit> =
    task {
        // Fetch through REST; DM channels are usually missing from the gateway cache.
        let! channel = client.Rest.GetChannelAsync(request.ChannelId)
        let channel = channel :> IChannel

        match action request.Emote channel guildId, channel with
        | Action.Save, (:? IMessageChannel as messages) -> do! save client messages request
        | Action.Delete, (:? IMessageChannel as messages) -> do! delete client messages request
        | _ -> ()
    }

/// Queue matching reactions immediately; channel and message checks happen in the worker.
let subscribe (client: DiscordSocketClient) (requests: MailboxProcessor<Request>) : unit =
    let handler
        (_: Cacheable<IUserMessage, uint64>)
        (channel: Cacheable<IMessageChannel, uint64>)
        (reaction: SocketReaction)
        : Task =
        let fromBot = reaction.User.IsSpecified && reaction.User.Value.IsBot

        if not fromBot && isEmoji (saveEmoji + deleteEmoji) reaction.Emote then
            requests.Post(
                {
                    ChannelId = channel.Id
                    MessageId = reaction.MessageId
                    UserId = reaction.UserId
                    Emote = reaction.Emote
                }
            )

        Task.CompletedTask

    client.add_ReactionAdded (
        Func<Cacheable<IUserMessage, uint64>, Cacheable<IMessageChannel, uint64>, SocketReaction, Task>(handler)
    )

let start: Feature =
    fun context ->
        Worker.start
            (fun (request: Request) -> $"Bookmark {request.Emote} for {request.ChannelId}/{request.MessageId}")
            (fun request -> handle context.Client context.GuildId request)
        |> subscribe context.Client

        Task.FromResult []
