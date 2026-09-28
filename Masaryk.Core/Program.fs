open Discord
open Discord.WebSocket
open System.Threading.Tasks
open System.Threading
open System
open Masaryk.Core

let client = new DiscordSocketClient()

let guildId =
    let value = Environment.GetEnvironmentVariable("GUILD_ID")

    match UInt64.TryParse(value) with
    | true, id when id > 0UL -> id
    | _ -> invalidOp "GUILD_ID environment variable must be a positive integer."

let log (message: LogMessage) : Task = task { printfn $"{message}" }

let buildSlashCommand (input: Command) : SlashCommandProperties =
    SlashCommandBuilder().WithName(input.Name).WithDescription(input.Description).Build()

let registerCommands (guild: SocketGuild) : Task =
    task {
        let properties =
            CommandRegistry.commands
            |> List.map (fun command -> buildSlashCommand command :> ApplicationCommandProperties)
            |> List.toArray

        let! _ = guild.BulkOverwriteApplicationCommandAsync(properties)
        return ()
    }

let clientReady () : Task =
    task {
        match client.GetGuild(guildId) with
        | null -> invalidOp $"Guild {guildId} was not found after the client became ready."
        | guild -> do! registerCommands guild
    }


let slashCommandHandler (incomingCommand: SocketSlashCommand) : Task =
    task {
        match Map.tryFind incomingCommand.CommandName CommandRegistry.commandsByName with
        | Some command -> do! command.Handle incomingCommand
        | None -> do! incomingCommand.RespondAsync("unknown command")
    }

let connect () =
    task {
        client.add_Log (Func<LogMessage, Task>(log))
        client.add_Ready (Func<Task>(clientReady))
        client.add_SlashCommandExecuted (Func<SocketSlashCommand, Task>(slashCommandHandler))

        let token = Environment.GetEnvironmentVariable("BOT_TOKEN")

        if String.IsNullOrWhiteSpace token then
            invalidOp "BOT_TOKEN environment variable is required."

        do! client.LoginAsync(TokenType.Bot, token)
        do! client.StartAsync()

        do! Task.Delay(Timeout.Infinite)
    }

[<EntryPoint>]
let main args =
    printfn "Hello, World!"
    connect().GetAwaiter().GetResult()
    0
