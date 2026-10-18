/// `/config export` and `/config import`: every server setting as one file.
module Masaryk.Core.Services.Config

open System.IO
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Core.Options
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

/// The admins' log channel, if one is set and the bot can see it.
let logChannel (context: Context) : ITextChannel option =
    context.Configuration.Current.LogChannel
    |> Option.bind (fun id -> tryUnbox<ITextChannel>(context.Client.GetChannel id))

let private everyoneCanSee (channel: SocketTextChannel) =
    let everyone = channel.Guild.EveryoneRole

    let denied =
        channel.GetPermissionOverwrite(everyone)
        |> Option.ofNullable
        |> Option.exists (fun overwrite -> overwrite.ViewChannel = PermValue.Deny)

    everyone.Permissions.ViewChannel && not denied

/// Settings that point at channels this server doesn't have.
let private missingChannels (guild: SocketGuild) (configuration: Configuration) =
    let exists id = not (isNull (guild.GetChannel id))

    [
        match configuration.LogChannel with
        | Some id when isNull (guild.GetTextChannel id) -> $"logChannel: there's no text channel {id} on this server"
        | _ -> ()
        for board in configuration.Boards do
            if not (exists board.Target) then
                $"starboard '{board.Name}' target: there's no channel {board.Target} on this server"

            for id in board.Channels do
                if not (exists id) then
                    $"starboard '{board.Name}' channels: there's no channel {id} on this server"
    ]

let private http = new HttpClient()

let private refused (problems: string list) =
    "Nothing was saved. Fix these and import again:\n- "
    + String.concat "\n- " problems

let private export (context: Context) (interaction: SocketSlashCommand) : Task =
    task {
        use stream =
            new MemoryStream(
                Encoding.UTF8.GetBytes(Document.export context.Configuration.Version context.Configuration.Current)
            )

        do!
            interaction.RespondWithFileAsync(
                new FileAttachment(stream, "masaryk-config.jsonc"),
                $"Version {context.Configuration.Version}. Edit it and send it back with `/config import`.",
                ephemeral = true
            )
    }

let private import (context: Context) (repository: ConfigurationRepository) (interaction: SocketSlashCommand) =
    task {
        do! interaction.DeferAsync(ephemeral = true)

        let attachment = (snd (input interaction) "file").Value :?> IAttachment

        let! reply =
            task {
                if attachment.Size > 256_000 then
                    return "That file is too big for a configuration."
                else
                    let! text = http.GetStringAsync attachment.Url
                    let guild = context.Client.GetGuild context.GuildId

                    match Document.read text with
                    | Error problems -> return refused problems
                    | Ok(None, _) ->
                        return "The file has no `version`. Start from a fresh `/config export` and edit that."
                    | Ok(Some basedOn, configuration) ->
                        match missingChannels guild configuration with
                        | [] ->
                            match!
                                repository.Save(
                                    context.GuildId,
                                    basedOn,
                                    Document.store configuration,
                                    interaction.User.Id
                                )
                            with
                            | Saved version ->
                                do! context.Configuration.Reload()

                                let warning =
                                    match configuration.LogChannel |> Option.map guild.GetTextChannel with
                                    | Some channel when everyoneCanSee channel ->
                                        $"\nEveryone can see <#{channel.Id}>; restrict it to admins."
                                    | _ -> ""

                                return $"Saved and applied version {version}.{warning}"
                            | Stale current ->
                                return
                                    $"Someone saved version {current} after you exported version {basedOn}, so nothing was saved. Export again and redo your changes."
                        | problems -> return refused problems
            }

        do! interaction.FollowupAsync(MessageEmbed.shorten 2000 reply, ephemeral = true) :> Task
    }

let start: Feature =
    fun context ->
        task {
            let repository = ConfigurationRepository(context.Database)

            let handle (interaction: SocketSlashCommand) : Task =
                match fst (input interaction) with
                | "export" -> export context interaction
                | _ -> import context repository interaction

            return
                [
                    {
                        Name = "config"
                        Description = "This server's bot configuration"
                        Options =
                            [
                                subcommand "export" "Get the configuration as a file to edit" []
                                subcommand
                                    "import"
                                    "Check and apply an edited configuration file"
                                    [
                                        (option
                                            "file"
                                            "The file from /config export, edited"
                                            ApplicationCommandOptionType.Attachment)
                                            .WithRequired(true)
                                    ]
                            ]
                        Permission = Some GuildPermission.Administrator
                        Handle = handle
                    }
                ]
        }
