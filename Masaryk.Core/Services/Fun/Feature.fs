module Masaryk.Core.Services.Fun.Feature

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Core.Options

let private command name description options handle : Command =
    {
        Name = name
        Description = description
        Options = options
        Permission = None
        Handle = handle
    }

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
                [
                    (option "emoji" "A custom emoji" ApplicationCommandOptionType.String).WithRequired(true)
                ]
            subcommand "list" "All of this server's emoji" []
        ]
        (fun interaction ->
            task {
                let name, value = input interaction

                if name = "list" then
                    // One message holds 2,000 characters, so long emoji lists continue in follow-ups.
                    let messages =
                        guild().Emotes
                        |> Seq.sortBy _.Name
                        |> Seq.map string
                        |> Seq.chunkBySize 10
                        |> Seq.map (String.concat " ")
                        |> Seq.fold
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
                            do! interaction.FollowupAsync(message) :> Task
                else
                    match Emote.TryParse(string (value "emoji").Value) with
                    | true, emote -> do! image interaction emote.Name emote.Url
                    | _ -> do! refuse interaction "That isn't a custom emoji."
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
            option "user" "Whose avatar (default: yours)" ApplicationCommandOptionType.User
        ]
        (fun interaction ->
            let _, value = input interaction

            let user =
                value "user" |> Option.map unbox<IUser> |> Option.defaultValue interaction.User

            image interaction (Format.Sanitize user.Username) (user.GetDisplayAvatarUrl(size = 1024us)))

let private cat =
    command
        "cat"
        "An HTTP status code, explained by a cat"
        [
            (option "code" "HTTP status code" ApplicationCommandOptionType.Integer)
                .WithRequired(true)
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
        Task.FromResult [ emoji guild; server guild; avatar; cat ]
