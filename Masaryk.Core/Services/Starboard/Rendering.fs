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

let private replyEmoji = "↪️"

let private chainLine (reply: ReplySnapshot) : string =
    match reply.AuthorDisplayName, reply.Content with
    | Some name, Some content ->
        // Flattened, without code fences, so a cut-off message can't swallow the lines below it.
        let text =
            if String.IsNullOrWhiteSpace content then
                "*no text*"
            else
                content.Replace("```", "").ReplaceLineEndings(" ") |> shorten 200

        $"[{Format.Sanitize(shorten 80 name)}]({reply.SourceJumpUrl}): {text}"
    | _ -> $"[Deleted message]({reply.SourceJumpUrl})"

/// Render one post, preserving attachment links and making any presentation limits visible.
let create (entry: Entry) : Post =
    let snapshot = entry.Snapshot

    let hasChain = not snapshot.Replies.IsEmpty || snapshot.RepliesTruncated

    let primary =
        MessageEmbed.build
            ""
            {
                AuthorName = snapshot.AuthorDisplayName
                AuthorAvatarUrl = snapshot.AuthorAvatarUrl
                CreatedAt = snapshot.SourceCreatedAt
                Content =
                    if hasChain then
                        $"{replyEmoji} {snapshot.Content}"
                    else
                        snapshot.Content
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

    if hasChain then
        // The starred message has priority; keep the newest replies that fit above it.
        let room = EmbedBuilder.MaxDescriptionLength - primary.Description.Length
        let lines = snapshot.Replies |> List.rev |> List.map chainLine

        let fitting =
            lines
            |> List.scan (fun used line -> used + line.Length + 4) 2
            |> List.tail
            |> List.takeWhile (fun used -> used <= room)
            |> List.length

        let omitted =
            if fitting < lines.Length || snapshot.RepliesTruncated then
                [ "…" ]
            else
                []

        let chain =
            omitted @ List.rev (List.take fitting lines)
            |> List.mapi (fun i line -> if i = 0 then line else $"{replyEmoji} {line}")

        primary.Description <- String.concat "\n" (chain @ [ primary.Description ])

    let emoji =
        if snapshot.QualifyingEmojiKey.StartsWith("custom:") then
            let id = snapshot.QualifyingEmojiKey.Substring("custom:".Length)
            $"<:{snapshot.QualifyingEmojiName}:{id}>"
        else
            snapshot.QualifyingEmojiName

    primary.AddField("Reactions · Channel", $"{emoji} **{snapshot.QualifyingCount}** · <#{snapshot.SourceChannelId}>")
    |> ignore

    {
        Embeds = [| primary.Build() |]
        Components =
            ComponentBuilder()
                .WithButton("Jump to message", style = ButtonStyle.Link, url = snapshot.SourceJumpUrl)
                .Build()
    }
