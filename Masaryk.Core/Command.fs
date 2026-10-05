namespace Masaryk.Core

open System.Threading.Tasks
open Discord
open Discord.WebSocket

type Command =
    {
        Name: string
        Description: string
        Options: SlashCommandOptionBuilder list
        Handle: SocketSlashCommand -> Task
    }
