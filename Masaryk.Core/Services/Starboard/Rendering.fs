module Masaryk.Core.Services.Starboard.Rendering

open System
open Discord
open Masaryk.Core.Services.Shared
open Masaryk.Core.Services.Starboard

let private replyEmoji = "↪️"

let reactionsField = "Reactions · Channel"

let reactions (emoji: string) (count: int) (channelId: uint64) = $"{emoji} **{count}** · <#{channelId}>"

let private chainLine (message: IMessage) : string =
    // Flattened, without code fences, so a cut-off message can't swallow the lines below it.
    let text =
        if String.IsNullOrWhiteSpace message.Content then
            "*no text*"
        else
            message.Content.Replace("```", "").ReplaceLineEndings(" ")
            |> MessageEmbed.shorten 200

    let name =
        MessageEmbed.authorName message.Author
        |> MessageEmbed.shorten 80
        |> Format.Sanitize

    $"[{name}]({message.GetJumpUrl()}): {text}"

/// The post for a starred message. `parents` is its reply chain, oldest first.
let create (message: IMessage) (parents: IMessage list) (reaction: QualifyingReaction) : Embed * MessageComponent =
    let source = MessageEmbed.fromMessage message

    let embed =
        MessageEmbed.build
            ""
            (if parents.IsEmpty then
                 source
             else
                 { source with
                     Content = $"{replyEmoji} {source.Content}"
                 })

    let room = EmbedBuilder.MaxDescriptionLength - embed.Description.Length

    // The chain needs room for at least "…" and a line break above the starred message.
    if not parents.IsEmpty && room >= 2 then
        // The starred message has priority; keep the newest replies that fit above it.
        let lines = parents |> List.rev |> List.map chainLine

        let fitting =
            lines
            |> List.scan (fun used line -> used + line.Length + 4) 2
            |> List.tail
            |> List.takeWhile (fun used -> used <= room)
            |> List.length

        // Replies that don't fit, or that are older than the fetched chain, become "…".
        let omitted =
            if fitting < lines.Length || (Replies.parentId parents.Head).IsSome then
                [ "…" ]
            else
                []

        let chain =
            omitted @ List.rev (List.take fitting lines)
            |> List.mapi (fun i line -> if i = 0 then line else $"{replyEmoji} {line}")

        embed.Description <- String.concat "\n" (chain @ [ embed.Description ])

    embed.AddField(reactionsField, reactions reaction.Emoji.Text reaction.EligibleCount message.Channel.Id)
    |> ignore

    embed.Build(), MessageEmbed.jumpButton message
