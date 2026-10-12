module Masaryk.Core.Starboard.Rendering

open System
open System.Text.Json
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
        value.Substring(0, limit - 1) + "…"

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

/// Read an embed's text and first image so embed-only messages still have content.
let private embedContent (json: string) : string * string option =
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

    let content =
        [ text "title" root; text "description" root; fields ]
        |> List.choose id
        |> String.concat "\n"

    let image =
        [ "image"; "thumbnail" ]
        |> List.tryPick (fun name -> property name root |> Option.bind (text "url"))

    content, image

/// Render one post, preserving attachment links and making any presentation limits visible.
let create (entry: Entry) : Post =
    let snapshot = entry.Snapshot
    let sourceEmbeds = snapshot.Embeds |> List.map embedContent

    let content =
        snapshot.Content :: (sourceEmbeds |> List.map fst)
        |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> String.concat "\n\n"

    let attachments =
        snapshot.Attachments
        |> List.map (fun file ->
            let name =
                file.Filename.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")

            $"[{name}](<{file.Url}>)")
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

    let image =
        snapshot.Attachments
        |> List.tryFind (fun file -> file.MediaType |> Option.exists (fun value -> value.StartsWith("image/")))
        |> Option.map _.Url
        |> Option.orElseWith (fun () -> sourceEmbeds |> List.tryPick snd)

    image |> Option.iter (fun url -> primary.WithImageUrl(imageUrl url) |> ignore)

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
