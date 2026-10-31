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

            let jobs = JobRepository(context.Database)
            Refresh.run context jobs |> ignore

            let server () = context.Client.GetGuild context.GuildId

            let prune now =
                Prune.command repository (settings ()) context.Client (server ()) now

            let cleanup () : Task =
                task {
                    // Members who left can only be told apart once the member list has loaded.
                    let mutable waited = 0

                    while (isNull (server ()) || not (server ()).HasAllMembers) && waited < 60 do
                        do! Task.Delay(TimeSpan.FromSeconds 10.0)
                        waited <- waited + 1

                    let finished = TaskCompletionSource()

                    queue "Daily course cleanup" (fun () ->
                        task {
                            try
                                do! Prune.daily context repository (settings ()) (server ())
                            finally
                                finished.SetResult()
                        })

                    do! finished.Task
                }

            Schedule.daily jobs "Course cleanup" (TimeOnly(4, 30)) cleanup |> ignore

            return
                [
                    SlashCommand.command repository guild settings queue prune
                    MemberCommand.command repository guild settings queue
                ]
        }
