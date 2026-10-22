/// `/touch-grass`: lock yourself out of the server for a while, e.g. to study.
module Masaryk.Core.Services.Lockout.Feature

open System
open System.Threading.Tasks
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Postgres

let start: Feature =
    fun context ->
        task {
            let repository = LockoutRepository(context.Database)
            let hidden: LockoutSettings option ref = ref None

            context.Client.add_ButtonExecuted (
                Func<SocketMessageComponent, Task>(SlashCommand.onButton context hidden repository)
            )

            Upkeep.run context repository hidden |> ignore

            return SlashCommand.commands context hidden repository
        }
