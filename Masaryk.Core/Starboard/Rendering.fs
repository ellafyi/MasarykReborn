module Masaryk.Core.Starboard.Rendering

open System
open System.Text.Json
open System.Text.RegularExpressions
open Discord
open Masaryk.Postgres.Starboard

type Post =
    {
        Embeds: Embed array
        Components: MessageComponent
    }

let private shorten (limit: int) (value: string) : string =
    if value.Length <= limit then
        value
    else
        // Don't split an emoji or other surrogate pair; Discord would show a replacement character.
        let cut = limit - 1

        let cut =
            if cut > 0 && Char.IsHighSurrogate value[cut - 1] then
                cut - 1
            else
                cut

        value.Substring(0, cut) + "…"

/// Discord marks spoiler attachments with this filename prefix.
let private isSpoiler (file: AttachmentSnapshot) : bool =
    file.Filename.StartsWith("SPOILER_", StringComparison.Ordinal)

/// Strip parameters only when rendering a Discord attachment URL in an image field.
let imageUrl (url: string) : string =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, uri when
        uri.Scheme = "https"
        && (uri.Host = "cdn.discordapp.com" || uri.Host = "media.discordapp.net")
        && uri.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal)
        ->
        uri.GetLeftPart(UriPartial.Path)
    | _ -> url

let private property (name: string) (element: JsonElement) : JsonElement option =
    match element.TryGetProperty name with
    | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
    | _ -> None

let private text (name: string) (element: JsonElement) : string option =
    property name element |> Option.map _.GetString()

type private SourceEmbed =
    {
        Text: string
        Image: string option
        Url: string option
    }

/// Read an embed's text and first image so embed-only messages still have content.
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

/// Text inside ||spoiler|| markers, used to hide previews of spoilered links.
let private spoilers (content: string) : string list =
    Regex.Matches(content, @"\|\|(.+?)\|\|", RegexOptions.Singleline)
    |> Seq.map _.Groups[1].Value
    |> Seq.toList

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
    let hidden = spoilers snapshot.Content

    // A link preview for a spoilered URL would reveal what the spoiler hides.
    let sourceEmbeds =
        snapshot.Embeds
        |> List.map embedContent
        |> List.filter (fun embed ->
            not (
                embed.Url
                |> Option.exists (fun url -> hidden |> List.exists (fun span -> span.Contains(url)))
            ))

    let content =
        snapshot.Content :: (sourceEmbeds |> List.map _.Text)
        |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> String.concat "\n\n"

    let attachments =
        snapshot.Attachments
        |> List.map (fun file ->
            let name =
                file.Filename.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")

            let link = $"[{name}](<{file.Url}>)"
            if isSpoiler file then $"||{link}||" else link)
        |> String.concat "\n"

    let attachmentText = if attachments = "" then "" else "\n\n" + attachments
    let contentLimit = EmbedBuilder.MaxDescriptionLength - attachmentText.Length

    if contentLimit < 1 then
        invalidOp "Attachment links exceed Discord's description limit; the full snapshot remains saved."

    let shortened = content.Length > contentLimit
    let description = shorten contentLimit content + attachmentText
    let marker = $"Starboard entry {entry.Id}"

    let footer =
        if shortened then
            marker + " · Text shortened to fit Discord"
        else
            marker

    let primary =
        EmbedBuilder()
            .WithAuthor(
                shorten EmbedAuthorBuilder.MaxAuthorNameLength snapshot.AuthorDisplayName,
                iconUrl = Option.toObj snapshot.AuthorAvatarUrl
            )
            .WithColor(Color.Gold)
            .WithTimestamp(DateTimeOffset(snapshot.SourceCreatedAt))
            .WithFooter(footer)

    if not (String.IsNullOrWhiteSpace description) then
        primary.WithDescription(description) |> ignore

    // Embed images can't be blurred, so spoiler attachments stay as spoilered links only.
    let image =
        snapshot.Attachments
        |> List.tryFind (fun file ->
            not (isSpoiler file)
            && file.MediaType |> Option.exists (fun value -> value.StartsWith("image/")))
        |> Option.map _.Url
        |> Option.orElseWith (fun () -> sourceEmbeds |> List.tryPick _.Image)

    image |> Option.iter (fun url -> primary.WithImageUrl(imageUrl url) |> ignore)

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
