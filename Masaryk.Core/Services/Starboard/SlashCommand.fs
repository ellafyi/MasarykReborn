module Masaryk.Core.Services.Starboard.SlashCommand

open System.Threading.Tasks
open Discord
open Discord.Net
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Core.Services
open Masaryk.Core.Services.Starboard
open Masaryk.Postgres

let private summary (from: IChannel) (board: Board) (result: Import.Result) =
    $"Starboard import from <#{from.Id}> into {board.Name}: read {result.Read} messages, found {result.Found} old posts, {result.Added} new."

/// Progress goes to the log channel when one is set, otherwise to the private reply.
let private import
    (context: Context)
    (repository: StarboardRepository)
    (interaction: SocketSlashCommand)
    (from: IMessageChannel)
    (board: Board)
    =
    task {
        let! show =
            match Config.logChannel context with
            | Some log ->
                task {
                    let! status =
                        log.SendMessageAsync($"Starboard import from <#{from.Id}> into {board.Name}: starting.")

                    let! _ =
                        interaction.FollowupAsync(
                            $"Reading <#{from.Id}>; progress is in <#{log.Id}>.",
                            ephemeral = true
                        )

                    return fun (text: string) -> status.ModifyAsync(fun message -> message.Content <- text)
                }
            | None ->
                Task.FromResult(fun (text: string) ->
                    interaction.ModifyOriginalResponseAsync(fun message -> message.Content <- text) :> Task)

        let progress (result: Import.Result) =
            if result.Read % 500 = 0 then
                show (summary from board result + " Still reading…")
            else
                Task.CompletedTask

        try
            let! result = Import.read repository context.GuildId from board.Target progress
            do! show (summary from board result + " Done; their originals won't be posted again.")
        with :? HttpException as error ->
            do! show $"Starboard import from <#{from.Id}> stopped: {error.Reason}. Running it again continues safely."
    }

let command (context: Context) (repository: StarboardRepository) : Command =
    let handle (interaction: SocketSlashCommand) : Task =
        task {
            do! interaction.DeferAsync(ephemeral = true)
            let subcommand = Seq.head interaction.Data.Options

            let channel name =
                subcommand.Options |> Seq.find (fun o -> o.Name = name) |> _.Value :?> IChannel

            let from = channel "from"
            let target = channel "to"
            let boards = context.Configuration.Current.Boards |> List.filter _.IsActive

            match boards |> List.tryFind (fun board -> board.Target = target.Id), from with
            | Some board, (:? IMessageChannel as from) -> do! import context repository interaction from board
            | _ ->
                let targets =
                    boards
                    |> List.map (fun board -> $"{board.Name} (<#{board.Target}>)")
                    |> String.concat ", "

                let! _ =
                    interaction.FollowupAsync(
                        $"<#{target.Id}> isn't a starboard channel. Boards in `/config`: {targets}.",
                        ephemeral = true
                    )

                ()
        }

    let channel (name: string) (description: string) =
        SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Channel)
            .AddChannelType(ChannelType.Text)
            .WithRequired(true)

    {
        Name = "starboard"
        Description = "Manage this server's starboards"
        Options =
            [
                SlashCommandOptionBuilder()
                    .WithName("import")
                    .WithDescription("Read the old bot's posts so their originals aren't posted again")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(channel "from" "A channel the old bot posted starboard messages in")
                    .AddOption(channel "to" "The board's channel in this bot's configuration")
            ]
        Permission = Some GuildPermission.Administrator
        Handle = handle
    }
