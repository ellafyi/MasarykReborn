module Masaryk.Core.Services.Starboard.Capture

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core.Services.Shared
open Masaryk.Core.Services.Starboard
open Masaryk.Core.Services.Starboard.DiscordAdapter
open Masaryk.Postgres

let private channel (client: DiscordSocketClient) (id: uint64) : Task<IMessageChannel> =
    task {
        let! channel = client.Rest.GetChannelAsync(id)
        return (channel :> IChannel) :?> IMessageChannel
    }

let private post
    (client: DiscordSocketClient)
    (repository: StarboardRepository)
    (board: Board)
    (message: IMessage)
    (reaction: QualifyingReaction)
    : Task<unit> =
    task {
        let! parents =
            Replies.ancestors (fun id -> message.Channel.GetMessageAsync(id)) 16 message

        let embed, components = Rendering.create message parents reaction
        let! target = channel client board.Target

        let! sent =
            target.SendMessageAsync(embed = embed, components = components, allowedMentions = AllowedMentions.None)

        do!
            repository.Save(
                message.Id,
                {
                    TargetChannelId = board.Target
                    PostMessageId = sent.Id
                    Emoji = reaction.Emoji.Text
                    Count = reaction.EligibleCount
                }
            )

        printfn $"Starboard {board.Name}: posted {message.GetJumpUrl()}."
    }

/// Recount the posted emoji and edit the count shown in the post.
let private update
    (client: DiscordSocketClient)
    (repository: StarboardRepository)
    (reaction: ReactionEvent)
    (posted: StarboardPost)
    : Task<unit> =
    task {
        let emote =
            match Emote.TryParse posted.Emoji with
            | true, custom -> custom :> IEmote
            | _ -> Discord.Emoji posted.Emoji

        if (emoji emote).Key = reaction.Emoji then
            let! source = channel client reaction.ChannelId
            let! message = source.GetMessageAsync(reaction.MessageId)
            let! target = channel client posted.TargetChannelId
            let! post = target.GetMessageAsync(posted.PostMessageId)

            match message, post with
            | null, _ -> ()
            | message, (:? IUserMessage as post) ->
                let! reactors =
                    match
                        message.Reactions
                        |> Seq.tryFind (fun pair -> (emoji pair.Key).Key = reaction.Emoji)
                    with
                    | Some pair -> readReactors message pair.Key pair.Value
                    | None -> Task.FromResult []

                let count = Evaluation.eligibleCount message.Author.Id reactors

                if count <> posted.Count then
                    let embeds =
                        post.Embeds
                        |> Seq.map (fun embed ->
                            let builder = embed.ToEmbedBuilder()

                            for field in builder.Fields do
                                if field.Name = Rendering.reactionsField then
                                    field.Value <- Rendering.reactions posted.Emoji count message.Channel.Id

                            builder.Build())
                        |> Seq.toArray

                    do! post.ModifyAsync(fun properties -> properties.Embeds <- embeds)
                    do! repository.SetCount(message.Id, count)
            // The post was deleted.
            | _ -> ()
    }

let handle
    (client: DiscordSocketClient)
    (repository: StarboardRepository)
    (guildId: uint64)
    (boards: Board list)
    (reaction: ReactionEvent)
    : Task<unit> =
    task {
        match! repository.Find reaction.MessageId with
        | Some(Posted posted) -> do! update client repository reaction posted
        | Some Legacy -> ()
        | None when reaction.Added ->
            match! evaluate client guildId boards reaction with
            | Some(board, message, qualifying) -> do! post client repository board message qualifying
            | None -> ()
        | None -> ()
    }
