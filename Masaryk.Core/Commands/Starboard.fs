module Masaryk.Core.Commands.Starboard

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Npgsql
open Masaryk.Core
open Masaryk.Core.Starboard

let command (configuration: Runtime.ConfigurationStore) guildId (managerRoleIds: Set<uint64>) : Command =
    let handle (interaction: SocketSlashCommand) : Task =
        task {
            match interaction.User with
            | :? SocketGuildUser as user when
                user.Guild.Id = guildId
                && (user.Roles |> Seq.exists (fun role -> Set.contains role.Id managerRoleIds))
                ->
                do! interaction.DeferAsync(ephemeral = true)

                let! response =
                    task {
                        try
                            return! configuration.Reload()
                        with
                        | Configuration.InvalidConfiguration message ->
                            return $"Configuration was not applied: {message}"
                        | :? NpgsqlException as error ->
                            eprintfn $"Starboard reload failed: %s{error.Message}"
                            return "Starboard reload failed. Check the database configuration and bot logs."
                    }

                let! _ =
                    interaction.FollowupAsync(response, ephemeral = true, allowedMentions = AllowedMentions.None)

                ()
            | _ ->
                do!
                    interaction.RespondAsync(
                        "A designated starboard manager role is required in this guild.",
                        ephemeral = true
                    )
        }

    {
        Name = "starboard"
        Description = "Reload this guild's starboard configuration"
        Options =
            [
                SlashCommandOptionBuilder()
                    .WithName("reload")
                    .WithDescription("Apply the starboard JSON configuration from the database")
                    .WithType(ApplicationCommandOptionType.SubCommand)
            ]
        Handle = handle
    }
