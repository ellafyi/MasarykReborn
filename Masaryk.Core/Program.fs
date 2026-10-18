open Discord
open Discord.WebSocket
open System.Threading.Tasks
open System.Threading
open System
open Masaryk.Core
open Masaryk.Core.Services

let client =
    new DiscordSocketClient(
        DiscordSocketConfig(
            GatewayIntents =
                (GatewayIntents.Guilds
                 ||| GatewayIntents.GuildMessages
                 ||| GatewayIntents.GuildMessageReactions
                 ||| GatewayIntents.DirectMessageReactions
                 ||| GatewayIntents.MessageContent)
        )
    )

let guildId =
    let value = Environment.GetEnvironmentVariable("GUILD_ID")

    match UInt64.TryParse(value) with
    | true, id when id > 0UL -> id
    | _ -> invalidOp "GUILD_ID environment variable must be a positive integer."

let log (message: LogMessage) : Task = task { printfn $"{message}" }

let buildSlashCommand (input: Command) : SlashCommandProperties =
    let builder =
        SlashCommandBuilder()
            .WithName(input.Name)
            .WithDescription(input.Description)
            .AddOptions(List.toArray input.Options)

    input.Permission
    |> Option.iter (fun permission -> builder.WithDefaultMemberPermissions(permission) |> ignore)

    builder.Build()

let registerCommands (commands: Command list) (guild: SocketGuild) : Task =
    task {
        let properties =
            commands
            |> List.map (fun command -> buildSlashCommand command :> ApplicationCommandProperties)
            |> List.toArray

        let! _ = guild.BulkOverwriteApplicationCommandAsync(properties)
        return ()
    }

let clientReady (commands: Command list) () : Task =
    task {
        match client.GetGuild(guildId) with
        | null -> invalidOp $"Guild {guildId} was not found after the client became ready."
        | guild -> do! registerCommands commands guild
    }


/// A command's permission is only Discord's default; server settings can widen it, so check it here too.
let allowed (command: Command) (interaction: SocketSlashCommand) =
    match command.Permission, interaction.User with
    | None, _ -> true
    | Some permission, (:? SocketGuildUser as user) -> user.GuildPermissions.Has permission
    | Some _, _ -> false

let slashCommandHandler (commandsByName: Map<string, Command>) (incomingCommand: SocketSlashCommand) : Task =
    task {
        match Map.tryFind incomingCommand.CommandName commandsByName with
        | Some command when allowed command incomingCommand -> do! command.Handle incomingCommand
        | Some _ -> do! incomingCommand.RespondAsync("Only server administrators can use this.", ephemeral = true)
        | None -> do! incomingCommand.RespondAsync("unknown command")
    }

/// Adding a feature means adding it here.
let features: Feature list =
    [
        Starboard.Feature.start
        Bookmarks.start
        Chat.start
        Leaderboard.Feature.start
        Fun.Feature.start
        Courses.Feature.start
        Menu.Feature.start
    ]

let token () =
    let value = Environment.GetEnvironmentVariable("BOT_TOKEN")

    if String.IsNullOrWhiteSpace value then
        invalidOp "BOT_TOKEN environment variable is required."

    value

let run (context: Context) : Task<unit> =
    task {
        let mutable commands = CommandRegistry.builtIn

        for start in features do
            let! featureCommands = start context
            commands <- commands @ featureCommands

        let commandsByName = CommandRegistry.byName commands
        client.add_Log (Func<LogMessage, Task>(log))
        client.add_Ready (Func<Task>(clientReady commands))
        client.add_SlashCommandExecuted (Func<SocketSlashCommand, Task>(slashCommandHandler commandsByName))

        do! client.LoginAsync(TokenType.Bot, token ())
        do! client.StartAsync()
        do! Task.Delay(Timeout.Infinite)
    }

[<EntryPoint>]
let main (args: string array) : int =
    use database = Masaryk.Postgres.Database.fromEnvironment ()

    let work =
        match args with
        | [| "import-history" |] -> Leaderboard.Import.run database guildId (token ())
        | [| "import-courses"; path |] -> Courses.Import.run database path
        | _ ->
            run
                {
                    Client = client
                    GuildId = guildId
                    Database = database
                }

    work.GetAwaiter().GetResult()
    0
