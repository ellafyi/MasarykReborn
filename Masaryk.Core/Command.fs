namespace Masaryk.Core

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Npgsql

type Command =
    {
        Name: string
        Description: string
        Options: SlashCommandOptionBuilder list
        Handle: SocketSlashCommand -> Task
    }

/// Everything a feature gets at startup.
type Context =
    {
        Client: DiscordSocketClient
        GuildId: uint64
        Database: NpgsqlDataSource
    }

/// Starts a feature: it subscribes to the events it needs and returns its slash commands.
type Feature = Context -> Task<Command list>
