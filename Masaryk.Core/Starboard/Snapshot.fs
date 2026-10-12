module Masaryk.Core.Starboard.Snapshot

open System
open System.Text.Json
open Discord
open Masaryk.Core.Starboard
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

/// Copy the evaluated message into storage records. Keep original media URLs unchanged.
let create (boardId: int64) (message: IMessage) (qualification: QualifyingReaction) : MessageSnapshot =
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
    }
