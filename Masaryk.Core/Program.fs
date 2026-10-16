open Discord
open Discord.WebSocket
open System.Threading.Tasks
open System.Threading
open System
open Masaryk.Core
open Masaryk.Core.Services
open Masaryk.Core.Services.Shared

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
    SlashCommandBuilder()
        .WithName(input.Name)
        .WithDescription(input.Description)
        .AddOptions(List.toArray input.Options)
        .Build()

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


let slashCommandHandler (commandsByName: Map<string, Command>) (incomingCommand: SocketSlashCommand) : Task =
    task {
        match Map.tryFind incomingCommand.CommandName commandsByName with
        | Some command -> do! command.Handle incomingCommand
        | None -> do! incomingCommand.RespondAsync("unknown command")
    }

let connect
    (commands: Command list)
    (configuration: Starboard.Runtime.ConfigurationStore)
    (repository: Masaryk.Postgres.StarboardRepository)
    (chat: (Chat.Settings * Masaryk.Postgres.ChatRepository) option)
    (leaderboard: Masaryk.Postgres.LeaderboardRepository)
    : Task<unit> =
    task {
        use reactions =
            Worker.start
                (fun (reaction: Starboard.Events.ReactionEvent) ->
                    $"Starboard processing for {reaction.ChannelId}/{reaction.MessageId}")
                (fun reaction -> Starboard.Capture.handle repository client guildId configuration.Boards reaction)

        use bookmarks =
            Worker.start
                (fun (request: Bookmarks.Request) ->
                    $"Bookmark {request.Emote} for {request.ChannelId}/{request.MessageId}")
                (fun request -> Bookmarks.handle client guildId request)

        Starboard.DiscordAdapter.subscribe client guildId reactions
        Bookmarks.subscribe client bookmarks

        chat
        |> Option.iter (fun (settings, chatRepository) ->
            Worker.start
                (fun (message: IUserMessage) -> $"Chat reply to {message.Channel.Id}/{message.Id}")
                (fun message -> Chat.handle client settings chatRepository message)
            |> Chat.subscribe client guildId)

        Leaderboard.Counting.subscribe client guildId leaderboard

        let commandsByName = CommandRegistry.byName commands
        client.add_Log (Func<LogMessage, Task>(log))
        client.add_Ready (Func<Task>(clientReady commands))
        client.add_SlashCommandExecuted (Func<SocketSlashCommand, Task>(slashCommandHandler commandsByName))

        let token = Environment.GetEnvironmentVariable("BOT_TOKEN")

        if String.IsNullOrWhiteSpace token then
            invalidOp "BOT_TOKEN environment variable is required."

        do! client.LoginAsync(TokenType.Bot, token)
        do! client.StartAsync()

        do! Task.Delay(Timeout.Infinite)
    }

[<EntryPoint>]
let main (args: string array) : int =
    use database = Masaryk.Postgres.Database.fromEnvironment ()
    let repository = Masaryk.Postgres.StarboardRepository(database)
    use configuration = new Starboard.Runtime.ConfigurationStore(repository, guildId)
    printfn $"%s{configuration.Reload().GetAwaiter().GetResult()}"
    let managerRoleIds = Starboard.Configuration.managerRolesFromEnvironment ()

    let leaderboard = Masaryk.Postgres.LeaderboardRepository(database)
    let countingSince = leaderboard.StartCounting().GetAwaiter().GetResult()
    printfn $"Leaderboard: counting messages since {countingSince:u}."

    let commands =
        CommandRegistry.create
            (Commands.Starboard.command configuration guildId managerRoleIds)
            (Commands.Leaderboard.command leaderboard)

    let chat =
        Chat.fromEnvironment ()
        |> Option.map (fun settings -> settings, Masaryk.Postgres.ChatRepository(database))

    if chat.IsNone then
        printfn "Chat: disabled; set LLM_API_URL to enable it."

    (connect commands configuration repository chat leaderboard).GetAwaiter().GetResult()
    0
