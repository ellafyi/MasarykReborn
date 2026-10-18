module Masaryk.Core.Services.Courses.Feature

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Postgres

let start: Feature =
    fun context ->
        task {
            let repository = CourseRepository(context.Database)

            context.Client.add_AutocompleteExecuted (
                Func<SocketAutocompleteInteraction, Task>(SlashCommand.autocomplete repository)
            )

            context.Client.add_ButtonExecuted (Func<SocketMessageComponent, Task>(SlashCommand.turnPage repository))

            let guild () =
                context.Client.GetGuild context.GuildId :> IGuild

            return [ SlashCommand.command repository guild ]
        }
