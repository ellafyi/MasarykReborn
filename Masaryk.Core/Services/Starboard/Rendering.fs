module Masaryk.Core.Services.Starboard.Rendering

open System
open System.Text.Json
open Discord
open Masaryk.Core.Services.Shared
open Masaryk.Core.Services.Shared.MessageEmbed
open Masaryk.Postgres.Starboard

type Post =
    {
        Embeds: Embed array
        Components: MessageComponent
    }

let private property (name: string) (element: JsonElement) : JsonElement option =
    match element.TryGetProperty name with
    | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
    | _ -> None

let private text (name: string) (element: JsonElement) : string option =
    property name element |> Option.map _.GetString()

/// Read a stored embed's text and first image so embed-only messages still have content.
let private embedContent (json: string) : SourceEmbed =
    use document = JsonDocument.Parse json
    let root = document.RootElement

    let fields =
        property "fields" root
        |> Option.map (fun fields ->
            fields.EnumerateArray()
            |> Seq.map (fun field ->
                let name = field.GetProperty("name").GetString()
                let value = field.GetProperty("value").GetString()
                $"{name}: {value}")
            |> String.concat "\n")

    {
        Text =
            [ text "title" root; text "description" root; fields ]
            |> List.choose id
            |> String.concat "\n"
        Image =
            [ "image"; "thumbnail" ]
            |> List.tryPick (fun name -> property name root |> Option.bind (text "url"))
        Url = text "url" root
    }

/// One field per reply keeps a shortened code block or spoiler from swallowing the next reply.
/// The newest replies are kept when Discord's total embed limit forces older ones out.
let private replyContext (snapshot: MessageSnapshot) (budget: int) : Embed option =
    if snapshot.Replies.IsEmpty && not snapshot.RepliesTruncated then
        None
    else
        let title = "Reply context"
        let omittedNotice = "Earlier replies omitted to fit Discord."
        let truncatedNotice = "Reply chain truncated at capture."
        // Reserve room for both notices so they always fit after the fields are chosen.
        let mutable remaining =
            budget - title.Length - omittedNotice.Length - truncatedNotice.Length - 1

        let mutable fields = []
        let mutable candidates = List.rev snapshot.Replies

        while not candidates.IsEmpty do
            let reply = candidates.Head

            let name =
                reply.AuthorDisplayName
                |> Option.map (fun name -> Format.Sanitize(shorten 80 name))
                |> Option.defaultValue "Unavailable reply"

            let preview =
                match reply.Content with
                | None -> "*Deleted or inaccessible at capture.*"
                | Some content when String.IsNullOrWhiteSpace content -> "*No text content.*"
                | Some content -> shorten 300 content

            // The link comes first so an unclosed code block in the preview can't hide it.
            let value = $"[Jump to reply](<{reply.SourceJumpUrl}>)\n{preview}"
            let size = name.Length + value.Length

            if size > remaining || fields.Length = EmbedBuilder.MaxFieldCount then
                candidates <- []
            else
                fields <- (name, value) :: fields
                remaining <- remaining - size
                candidates <- candidates.Tail

        let notices =
            [
                if fields.Length < snapshot.Replies.Length then
                    omittedNotice
                if snapshot.RepliesTruncated then
                    truncatedNotice
            ]

        let embed = EmbedBuilder().WithTitle(title).WithColor(Color.Gold)

        if not notices.IsEmpty then
            embed.WithDescription(String.concat "\n" notices) |> ignore

        for name, value in fields do
            embed.AddField(name, value) |> ignore

        Some(embed.Build())

/// Render one post, preserving attachment links and making any presentation limits visible.
let create (entry: Entry) : Post =
    let snapshot = entry.Snapshot

    let primary =
        MessageEmbed.build
            $"Starboard entry {entry.Id}"
            {
                AuthorName = snapshot.AuthorDisplayName
                AuthorAvatarUrl = snapshot.AuthorAvatarUrl
                CreatedAt = snapshot.SourceCreatedAt
                Content = snapshot.Content
                Embeds = snapshot.Embeds |> List.map embedContent
                Attachments =
                    snapshot.Attachments
                    |> List.map (fun file ->
                        ({
                            Filename = file.Filename
                            Url = file.Url
                            MediaType = file.MediaType
                        }
                        : Attachment))
            }

    let emoji =
        if snapshot.QualifyingEmojiKey.StartsWith("custom:") then
            let id = snapshot.QualifyingEmojiKey.Substring("custom:".Length)
            $"<:{snapshot.QualifyingEmojiName}:{id}>"
        else
            snapshot.QualifyingEmojiName

    primary.AddField("Reactions · Channel", $"{emoji} **{snapshot.QualifyingCount}** · <#{snapshot.SourceChannelId}>")
    |> ignore

    let primary = primary.Build()
    let context = replyContext snapshot (EmbedBuilder.MaxEmbedLength - primary.Length)

    {
        Embeds =
            match context with
            | Some context -> [| context; primary |]
            | None -> [| primary |]
        Components =
            ComponentBuilder()
                .WithButton("Jump to message", style = ButtonStyle.Link, url = snapshot.SourceJumpUrl)
                .Build()
    }
