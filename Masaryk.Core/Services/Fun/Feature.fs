module Masaryk.Core.Services.Fun.Feature

open System
open System.IO
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core

let private asset (name: string) =
    use stream = typeof<Command>.Assembly.GetManifestResourceStream name
    use reader = new StreamReader(stream)
    reader.ReadToEnd()

let private nightSky = asset "night_sky.txt"

let private hugs =
    (asset "hugs.txt").Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

let private option (name: string) (description: string) (kind: ApplicationCommandOptionType) (required: bool) =
    SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(kind).WithRequired(required)

let private subcommand (name: string) (description: string) (options: SlashCommandOptionBuilder list) =
    let builder = option name description ApplicationCommandOptionType.SubCommand false

    for nested in options do
        builder.AddOption(nested) |> ignore

    builder

let private command name description options handle : Command =
    {
        Name = name
        Description = description
        Options = options
        Permission = None
        Handle = handle
    }

/// The chosen subcommand and the options the user filled in.
let private input (interaction: SocketSlashCommand) =
    let top = List.ofSeq interaction.Data.Options

    let name, options =
        match top with
        | [ only ] when only.Type = ApplicationCommandOptionType.SubCommand -> only.Name, List.ofSeq only.Options
        | _ -> "", top

    let value key =
        options |> List.tryFind (fun o -> o.Name = key) |> Option.map _.Value

    name, value

let private image (interaction: SocketSlashCommand) (title: string) (url: string) : Task =
    interaction.RespondAsync(embed = EmbedBuilder().WithTitle(title).WithUrl(url).WithImageUrl(url).Build())

let private refuse (interaction: SocketSlashCommand) (text: string) : Task =
    interaction.RespondAsync(text, ephemeral = true)

let private emoji (guild: unit -> SocketGuild) =
    command
        "emoji"
        "Custom emoji"
        [
            subcommand
                "image"
                "Show a custom emoji's image"
                [ option "emoji" "A custom emoji" ApplicationCommandOptionType.String true ]
            subcommand "list" "All of this server's emoji" []
        ]
        (fun interaction ->
            task {
                let name, value = input interaction

                let parsed =
                    match value "emoji" |> Option.map string |> Option.defaultValue "" |> Emote.TryParse with
                    | true, emote -> Some emote
                    | _ -> None

                match name, parsed with
                | "list", _ ->
                    // One message holds 2,000 characters, so long emoji lists continue in follow-ups.
                    let rows =
                        guild().Emotes
                        |> Seq.sortBy _.Name
                        |> Seq.map string
                        |> Seq.chunkBySize 10
                        |> Seq.map (String.concat " ")
                        |> Seq.toList

                    let messages =
                        rows
                        |> List.fold
                            (fun messages row ->
                                match messages with
                                | current :: rest when String.length current + row.Length < 1990 ->
                                    $"{current}\n{row}" :: rest
                                | _ -> row :: messages)
                            []
                        |> List.rev

                    match messages with
                    | [] -> do! interaction.RespondAsync("This server has no emoji.")
                    | first :: rest ->
                        do! interaction.RespondAsync(first)

                        for message in rest do
                            let! _ = interaction.FollowupAsync(message)
                            ()
                | _, None -> do! refuse interaction "That isn't a custom emoji."
                | _, Some emote -> do! image interaction emote.Name emote.Url
            })

let private server (guild: unit -> SocketGuild) =
    command
        "server"
        "This server's pictures"
        [
            subcommand "icon" "Show the server icon" []
            subcommand "banner" "Show the server banner" []
        ]
        (fun interaction ->
            let name, _ = input interaction
            let guild = guild ()
            let url = if name = "icon" then guild.IconUrl else guild.BannerUrl

            if isNull url then
                refuse interaction $"This server has no {name}."
            else
                image interaction $"{guild.Name} {name}" url)

let private avatar =
    command
        "avatar"
        "Show someone's avatar"
        [
            option "user" "Whose avatar (default: yours)" ApplicationCommandOptionType.User false
        ]
        (fun interaction ->
            let _, value = input interaction

            let user =
                value "user" |> Option.map unbox<IUser> |> Option.defaultValue interaction.User

            image interaction (Discord.Format.Sanitize user.Username) (user.GetDisplayAvatarUrl(size = 1024us)))

let private choose =
    command
        "choose"
        "Pick one of several options"
        [
            option "options" "Options separated by commas" ApplicationCommandOptionType.String true
        ]
        (fun interaction ->
            let _, value = input interaction

            let options =
                (value "options" |> Option.map string |> Option.defaultValue "").Split(',')
                |> Array.map _.Trim()
                |> Array.filter ((<>) "")

            if options.Length = 0 then
                refuse interaction "Give me some options, separated by commas."
            else
                let embed =
                    EmbedBuilder()
                        .WithTitle($"I choose {Random.Shared.GetItems(options, 1)[0]}")
                        .WithDescription(String.concat " / " options)

                interaction.RespondAsync(embed = embed.Build(), allowedMentions = AllowedMentions.None))

let private hug =
    command
        "hug"
        "Because everyone likes hugs"
        [
            option "user" "Who to hug" ApplicationCommandOptionType.User true
            (option "intensity" "Which hug (default: random)" ApplicationCommandOptionType.Integer false)
                .WithMinValue(0)
                .WithMaxValue(float (hugs.Length - 1))
        ]
        (fun interaction ->
            let _, value = input interaction
            let user = unbox<IUser> (value "user").Value

            let face =
                value "intensity"
                |> Option.map (fun index -> hugs[int (unbox<int64> index)])
                |> Option.defaultWith (fun () -> Random.Shared.GetItems(hugs, 1)[0])

            interaction.RespondAsync($"{face} {user.Mention}", allowedMentions = AllowedMentions.None))

let private answer =
    command
        "answer"
        "Yes or no?"
        [ option "question" "Your question" ApplicationCommandOptionType.String true ]
        (fun interaction ->
            let _, value = input interaction

            let embed =
                EmbedBuilder()
                    .WithTitle(Random.Shared.GetItems([| "Yes"; "No" |], 1)[0])
                    .WithDescription(string (value "question").Value)

            interaction.RespondAsync(embed = embed.Build(), allowedMentions = AllowedMentions.None))

let private nightsky =
    let cooldown = TimeSpan.FromMinutes 10.0
    let mutable last = DateTimeOffset.MinValue

    command "nightsky" "Look up at the night sky" [] (fun interaction ->
        let now = DateTimeOffset.UtcNow

        if now - last < cooldown then
            let again =
                TimestampTag.FromDateTimeOffset(last + cooldown, TimestampTagStyles.Relative)

            refuse interaction $"The sky needs a moment; try again {again}."
        else
            last <- now
            interaction.RespondAsync(nightSky))

let private cat =
    command
        "cat"
        "An HTTP status code, explained by a cat"
        [
            (option "code" "HTTP status code" ApplicationCommandOptionType.Integer true)
                .WithMinValue(100)
                .WithMaxValue(599)
        ]
        (fun interaction ->
            let _, value = input interaction
            let code = unbox<int64> (value "code").Value
            image interaction $"HTTP {code}" $"https://http.cat/{code}.jpg")

let start: Feature =
    fun context ->
        let guild () = context.Client.GetGuild context.GuildId
        Task.FromResult [ emoji guild; server guild; avatar; choose; hug; answer; nightsky; cat ]
