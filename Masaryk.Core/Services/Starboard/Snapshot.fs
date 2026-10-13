module Masaryk.Core.Services.Starboard.Snapshot

open System
open System.Collections.Generic
open System.Net
open System.Threading.Tasks
open System.Text.Json
open Discord
open Masaryk.Core.Services.Starboard
open Masaryk.Postgres.Starboard

let private jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let emojiKey (key: EmojiKey) : string =
    match key with
    | EmojiKey.Unicode value -> $"unicode:{value}"
    | EmojiKey.Custom id -> $"custom:{id}"

let authorName (author: IUser) : string =
    match author with
    | :? IGuildUser as memberUser -> memberUser.DisplayName
    | _ ->
        if String.IsNullOrEmpty author.GlobalName then
            author.Username
        else
            author.GlobalName

/// Follow only actual replies in the source channel; other references may expose unrelated content.
let private replyId (guildId: uint64) (channelId: uint64) (message: IMessage) : uint64 option =
    let reference = message.Reference

    if
        message.Type = MessageType.Reply
        && not (isNull reference)
        && reference.MessageId.IsSpecified
        && reference.ChannelId = channelId
        && (not reference.GuildId.IsSpecified || reference.GuildId.Value = guildId)
        && (not reference.ReferenceType.IsSpecified
            || reference.ReferenceType.Value = MessageReferenceType.Default)
    then
        Some reference.MessageId.Value
    else
        None

/// Freeze up to sixteen ancestors, oldest first, retaining links to unavailable messages.
let readReplies
    (fetch: uint64 -> Task<IMessage>)
    (guildId: uint64)
    (message: IMessage)
    : Task<ReplySnapshot list * bool> =
    task {
        let channelId = message.Channel.Id
        let visited = HashSet<uint64>()
        visited.Add message.Id |> ignore
        let mutable next = replyId guildId channelId message
        let mutable replies = []
        let mutable truncated = false

        while next.IsSome do
            let id = next.Value

            if visited.Count > 16 || not (visited.Add id) then
                truncated <- true
                next <- None
            else
                let! parent =
                    task {
                        try
                            return! fetch id
                        with :? Discord.Net.HttpException as error when
                            error.HttpCode = HttpStatusCode.NotFound
                            || error.HttpCode = HttpStatusCode.Forbidden ->
                            eprintfn $"Starboard reply {channelId}/{id} unavailable: {error.HttpCode}."
                            return null
                    }

                let reply =
                    {
                        SourceChannelId = channelId
                        SourceMessageId = id
                        AuthorDisplayName =
                            if isNull parent then
                                None
                            else
                                Some(authorName parent.Author)
                        Content =
                            if isNull parent then
                                None
                            else
                                Some(defaultArg (Option.ofObj parent.Content) "")
                        SourceJumpUrl = $"https://discord.com/channels/{guildId}/{channelId}/{id}"
                    }

                replies <- reply :: replies

                next <-
                    if isNull parent then
                        None
                    else
                        replyId guildId channelId parent

        return replies, truncated
    }

/// Copy the evaluated message into storage records. Keep original media URLs unchanged.
let create
    (boardId: int64)
    (message: IMessage)
    (qualification: QualifyingReaction)
    (replies: ReplySnapshot list)
    (repliesTruncated: bool)
    : MessageSnapshot =
    {
        BoardId = boardId
        SourceChannelId = message.Channel.Id
        SourceMessageId = message.Id
        SourceCreatedAt = message.Timestamp.UtcDateTime
        AuthorUserId = Some message.Author.Id
        AuthorDisplayName = authorName message.Author
        AuthorAvatarUrl = Option.ofObj (message.Author.GetDisplayAvatarUrl())
        Content = message.Content
        SourceJumpUrl = message.GetJumpUrl()
        QualifyingEmojiKey = emojiKey qualification.Emoji.Key
        QualifyingEmojiName = qualification.Emoji.DisplayName
        QualifyingCount = qualification.EligibleCount
        // Retain raw totals for every emoji, including those below the threshold.
        Reactions =
            message.Reactions
            |> Seq.map (fun (KeyValue(emote, metadata)) ->
                let emoji = DiscordAdapter.emoji emote

                {
                    EmojiKey = emojiKey emoji.Key
                    EmojiName = emoji.DisplayName
                    RawCount = metadata.ReactionCount
                })
            |> Seq.toList
        Attachments =
            message.Attachments
            |> Seq.map (fun attachment ->
                {
                    Id = attachment.Id
                    Filename = attachment.Filename
                    Url = attachment.Url
                    MediaType = Option.ofObj attachment.ContentType
                    SizeBytes = Some(int64 attachment.Size)
                })
            |> Seq.toList
        Embeds =
            message.Embeds
            |> Seq.map (fun embed -> JsonSerializer.Serialize<IEmbed>(embed, jsonOptions))
            |> Seq.toList
        Replies = replies
        RepliesTruncated = repliesTruncated
    }
