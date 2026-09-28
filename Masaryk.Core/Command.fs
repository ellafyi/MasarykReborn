namespace Masaryk.Core

open System.Threading.Tasks
open Discord.WebSocket

type Command =
    { Name: string
      Description: string
      Handle: SocketSlashCommand -> Task }
