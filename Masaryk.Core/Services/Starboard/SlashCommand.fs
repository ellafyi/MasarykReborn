module Masaryk.Core.Services.Starboard.SlashCommand

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Core.Services.Starboard

/// Server admins can change who may use this under Server Settings → Integrations.
let command (configuration: Configuration.ConfigurationStore) : Command =
    let handle (interaction: SocketSlashCommand) : Task =
        task {
            do! interaction.DeferAsync(ephemeral = true)

            let! response =
                task {
                    try
                        return! configuration.Reload()
                    with Configuration.InvalidConfiguration message ->
                        return $"Configuration was not applied: {message}"
                }

            let! _ = interaction.FollowupAsync(response, ephemeral = true)
            ()
        }

    {
        Name = "starboard"
        Description = "Reload this server's starboard configuration"
        Options =
            [
                SlashCommandOptionBuilder()
                    .WithName("reload")
                    .WithDescription("Apply the starboard JSON configuration from the database")
                    .WithType(ApplicationCommandOptionType.SubCommand)
            ]
        Permission = Some GuildPermission.ManageGuild
        Handle = handle
    }
