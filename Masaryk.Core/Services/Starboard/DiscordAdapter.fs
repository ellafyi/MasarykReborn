module Masaryk.Core.Services.Starboard.DiscordAdapter

open System
open System.Threading.Tasks
open Discord
open Discord.Rest
open Discord.WebSocket
open Masaryk.Core.Services.Starboard

type EvaluationResult =
    {
        Board: Board
        Message: IMessage
        Reactions: ReactionObservation list
        Qualification: QualifyingReaction option
    }

let emoji (emote: IEmote) : Masaryk.Core.Services.Starboard.Emoji =
    {
        Key =
            match emote with
            | :? Emote as custom -> EmojiKey.Custom custom.Id
            | _ -> EmojiKey.Unicode emote.Name
        DisplayName = emote.Name
    }

let sourceChannel (channel: IChannel) : SourceChannel option =
    match channel with
    | :? SocketThreadChannel as thread -> Some(SourceChannel.Thread(thread.Id, thread.ParentChannel.Id))
    | :? RestThreadChannel as thread -> Some(SourceChannel.Thread(thread.Id, thread.ParentChannelId))
    | :? ITextChannel -> Some(SourceChannel.Channel channel.Id)
    | _ -> None

/// Read every page for both reaction types. Eligibility rules deduplicate users by ID.
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

/// Fetch current message data, then evaluate the active board's eligible reactors.
let evaluate
    (client: DiscordSocketClient)
    (guildId: uint64)
    (boards: Board list)
    (reaction: Events.ReactionEvent)
    : Task<EvaluationResult option> =
    task {
        let! channel = client.Rest.GetChannelAsync(reaction.ChannelId)
        let channel = channel :> IChannel

        let board =
            match channel with
            | :? IGuildChannel as guildChannel when guildChannel.GuildId = guildId ->
                sourceChannel channel
                |> Option.bind (Evaluation.resolveBoard boards)
                |> Option.filter _.Settings.IsActive
            | _ -> None

        match board with
        | None -> return None
        | Some board ->
            // Always fetch through REST; gateway caches can miss old messages or recent reactions.
            let! message = (channel :?> IMessageChannel).GetMessageAsync(reaction.MessageId)

            if isNull message then
                eprintfn $"Starboard source message {reaction.ChannelId}/{reaction.MessageId} no longer exists."
                return None
            else
                let observations = ResizeArray<ReactionObservation>()

                for KeyValue(emote, metadata) in message.Reactions do
                    // Raw totals only decide which emoji could reach the threshold.
                    if metadata.ReactionCount >= board.Settings.Threshold then
                        let! reactors = readReactors message emote metadata

                        observations.Add(
                            {
                                Emoji = emoji emote
                                Reactors = reactors
                            }
                        )

                let observations = List.ofSeq observations
                let authorId = Some message.Author.Id

                return
                    Some
                        {
                            Board = board
                            Message = message
                            Reactions = observations
                            Qualification =
                                Evaluation.qualifyingReaction
                                    board.Settings.Threshold
                                    authorId
                                    (Some reaction.Emoji)
                                    observations
                        }
    }

let report (result: EvaluationResult) : unit =
    let counts =
        result.Reactions
        |> List.map (fun reaction ->
            let count =
                Evaluation.eligibleCount (Some result.Message.Author.Id) reaction.Reactors

            $"{reaction.Emoji.DisplayName}: {count} eligible")
        |> String.concat "; "

    let decision =
        match result.Qualification with
        | Some reaction -> $"qualifies with {reaction.Emoji.DisplayName} ({reaction.EligibleCount})"
        | None -> "does not qualify"

    let counts = if counts = "" then "raw totals below threshold" else counts

    printfn
        $"Starboard {result.Board.Settings.Name}: {result.Message.GetJumpUrl()} — {decision}; threshold {result.Board.Settings.Threshold}; {counts}"

/// Queue reaction additions immediately so Discord's gateway never waits for REST calls.
let subscribe
    (client: DiscordSocketClient)
    (guildId: uint64)
    (reactions: MailboxProcessor<Events.ReactionEvent>)
    : unit =
    let handler
        (_: Cacheable<IUserMessage, uint64>)
        (channel: Cacheable<IMessageChannel, uint64>)
        (reaction: SocketReaction)
        : Task =
        let belongsToGuild =
            if channel.HasValue then
                match channel.Value with
                | :? IGuildChannel as guildChannel -> guildChannel.GuildId = guildId
                | _ -> false
            else
                true

        if belongsToGuild then
            reactions.Post(
                {
                    ChannelId = channel.Id
                    MessageId = reaction.MessageId
                    Emoji = (emoji reaction.Emote).Key
                }
            )

        Task.CompletedTask

    client.add_ReactionAdded (
        Func<Cacheable<IUserMessage, uint64>, Cacheable<IMessageChannel, uint64>, SocketReaction, Task>(handler)
    )
