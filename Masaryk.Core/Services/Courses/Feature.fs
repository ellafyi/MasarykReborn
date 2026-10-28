module Masaryk.Core.Services.Courses.Feature

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let start: Feature =
    fun context ->
        task {
            let repository = CourseRepository(context.Database)

            let settings () = context.Configuration.Current.Courses

            let guild () =
                context.Client.GetGuild context.GuildId :> IGuild

            let worker =
                Worker.start (fun (describe: string, _) -> describe) (fun (_, work: unit -> Task) -> work ())

            let queue describe work = worker.Post(describe, work)

            context.Client.add_AutocompleteExecuted (
                Func<SocketAutocompleteInteraction, Task>(
                    SlashCommand.autocomplete repository (fun () -> settings().HomeFaculty)
                )
            )

            context.Client.add_ButtonExecuted (Func<SocketMessageComponent, Task>(SlashCommand.turnPage repository))

            context.Client.add_ButtonExecuted (
                Func<SocketMessageComponent, Task>(MemberCommand.onButton repository guild settings queue)
            )

            let left (server: SocketGuild) (user: SocketUser) : Task =
                if server.Id = context.GuildId then
                    queue $"Course cleanup for {user.Id}, who left" (fun () ->
                        task {
                            let! _ = Membership.leaveAll repository (settings ()) server user
                            ()
                        })

                Task.CompletedTask

            context.Client.add_UserLeft (Func<SocketGuild, SocketUser, Task>(left))

            Refresh.run context |> ignore

            let server () = context.Client.GetGuild context.GuildId

            let prune now =
                Prune.command repository (settings ()) context.Client (server ()) now

            let cleanup () : Task =
                task {
                    // Gives the member list time to load, so members who left can be told apart.
                    do! Task.Delay(TimeSpan.FromMinutes 10.0)

                    while true do
                        queue "Daily course cleanup" (fun () ->
                            Prune.daily context repository (settings ()) (server ()))

                        do! Task.Delay(TimeSpan.FromDays 1.0)
                }

            cleanup () |> ignore

            return
                [
                    SlashCommand.command repository guild settings queue prune
                    MemberCommand.command repository guild settings queue
                ]
        }
