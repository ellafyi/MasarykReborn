/// Render a Discord message as an embed, for services that repost or copy messages.
module Masaryk.Core.Services.Shared.MessageEmbed

open System
open System.Text.RegularExpressions
open Discord

type Attachment =
    {
        Filename: string
        Url: string
        MediaType: string option
    }

/// Text and media taken from one of the message's link previews or rich embeds.
type SourceEmbed =
    {
        Text: string
        Image: string option
        Url: string option
    }

type Message =
    {
        AuthorName: string
        AuthorAvatarUrl: string option
        /// UTC.
        CreatedAt: DateTime
        Content: string
        Embeds: SourceEmbed list
        Attachments: Attachment list
    }

/// Prefer the server nickname, then the global display name, then the username.
let authorName (author: IUser) : string =
    match author with
    | :? IGuildUser as memberUser -> memberUser.DisplayName
    | _ ->
        if String.IsNullOrEmpty author.GlobalName then
            author.Username
        else
            author.GlobalName

let shorten (limit: int) (value: string) : string =
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
let private isSpoiler (file: Attachment) : bool =
    file.Filename.StartsWith("SPOILER_", StringComparison.Ordinal)

/// Discord attachment URLs without their query parameters; other URLs unchanged.
let imageUrl (url: string) : string =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, uri when
        uri.Scheme = "https"
        && (uri.Host = "cdn.discordapp.com" || uri.Host = "media.discordapp.net")
        && uri.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal)
        ->
        uri.GetLeftPart(UriPartial.Path)
    | _ -> url

/// Read a live embed's text and first image so embed-only messages still have content.
let sourceEmbed (embed: IEmbed) : SourceEmbed =
    let fields = embed.Fields |> Seq.map (fun field -> $"{field.Name}: {field.Value}")

    {
        Text =
            Seq.append [ embed.Title; embed.Description ] fields
            |> Seq.filter (fun value -> not (String.IsNullOrEmpty value))
            |> String.concat "\n"
        Image =
            if embed.Image.HasValue then
                Some embed.Image.Value.Url
            elif embed.Thumbnail.HasValue then
                Some embed.Thumbnail.Value.Url
            else
                None
        Url = Option.ofObj embed.Url
    }

let fromMessage (message: IMessage) : Message =
    {
        AuthorName = authorName message.Author
        AuthorAvatarUrl = Option.ofObj (message.Author.GetDisplayAvatarUrl())
        CreatedAt = message.Timestamp.UtcDateTime
        Content = message.Content
        Embeds = message.Embeds |> Seq.map sourceEmbed |> Seq.toList
        Attachments =
            message.Attachments
            |> Seq.map (fun attachment ->
                {
                    Filename = attachment.Filename
                    Url = attachment.Url
                    MediaType = Option.ofObj attachment.ContentType
                })
            |> Seq.toList
    }

/// Text inside ||spoiler|| markers, used to hide previews of spoilered links.
let private spoilers (content: string) : string list =
    Regex.Matches(content, @"\|\|(.+?)\|\|", RegexOptions.Singleline)
    |> Seq.map _.Groups[1].Value
    |> Seq.toList

/// Build an embed with the author, time, text, every attachment link, and the first image.
/// The footer shows marker, if any, and says when text had to be shortened to fit.
let build (marker: string) (message: Message) : EmbedBuilder =
    let hidden = spoilers message.Content

    // A link preview for a spoilered URL would reveal what the spoiler hides.
    let embeds =
        message.Embeds
        |> List.filter (fun embed ->
            not (
                embed.Url
                |> Option.exists (fun url -> hidden |> List.exists (fun span -> span.Contains(url)))
            ))

    let content =
        message.Content :: (embeds |> List.map _.Text)
        |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> String.concat "\n\n"

    let attachments =
        message.Attachments
        |> List.map (fun file ->
            let name =
                file.Filename.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")

            let link = $"[{name}](<{file.Url}>)"
            if isSpoiler file then $"||{link}||" else link)
        |> String.concat "\n"

    let attachmentText = if attachments = "" then "" else "\n\n" + attachments
    let contentLimit = EmbedBuilder.MaxDescriptionLength - attachmentText.Length

    if contentLimit < 1 then
        invalidOp "Attachment links exceed Discord's description limit."

    let shortened = content.Length > contentLimit
    let description = shorten contentLimit content + attachmentText

    let footer =
        [ marker; (if shortened then "Text shortened to fit Discord" else "") ]
        |> List.filter ((<>) "")
        |> String.concat " · "

    let embed =
        EmbedBuilder()
            .WithAuthor(
                shorten EmbedAuthorBuilder.MaxAuthorNameLength message.AuthorName,
                iconUrl = Option.toObj message.AuthorAvatarUrl
            )
            .WithColor(Color.Gold)
            .WithTimestamp(DateTimeOffset(message.CreatedAt))

    if footer <> "" then
        embed.WithFooter(footer) |> ignore

    if not (String.IsNullOrWhiteSpace description) then
        embed.WithDescription(description) |> ignore

    // Embed images can't be blurred, so spoiler attachments stay as spoilered links only.
    message.Attachments
    |> List.tryFind (fun file ->
        not (isSpoiler file)
        && file.MediaType |> Option.exists (fun value -> value.StartsWith("image/")))
    |> Option.map _.Url
    |> Option.orElseWith (fun () -> embeds |> List.tryPick _.Image)
    |> Option.iter (fun url -> embed.WithImageUrl(imageUrl url) |> ignore)

    embed
