/// `/course` and the request button, which members use to join courses and ask for new channels.
module Masaryk.Core.Services.Courses.MemberCommand

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Core.Options
open Masaryk.Postgres

let command
    (repository: CourseRepository)
    (guild: unit -> IGuild)
    (settings: unit -> CourseSettings)
    (queue: SlashCommand.Queue)
    : Command =
    let handle (interaction: SocketSlashCommand) : Task =
        let subcommand, value = input interaction
        let settings = settings ()
        let guild = guild ()
        let user = box interaction.User :?> IGuildUser

        let withCourse (act: Course -> Task<string>) =
            task {
                let code = string (value "course").Value

                match! Membership.find repository settings.HomeFaculty code with
                | Some course -> return! act course
                | None -> return $"There's no course {code} in the catalog."
            }

        SlashCommand.reply queue true interaction $"/course {subcommand} by {user.Id}" (fun () ->
            match subcommand with
            | "join" -> withCourse (fun course -> Membership.join repository settings guild course user)
            | "leave" -> withCourse (fun course -> Membership.leave repository settings guild course user)
            | "leave-all" -> Membership.leaveAll repository settings guild user
            | "mine" -> Membership.mine repository settings user
            | _ -> withCourse (Membership.info repository settings guild))

    {
        Name = "course"
        Description = "Join course channels"
        Options =
            [
                subcommand
                    "join"
                    "See a course's channel, or say you're interested if it has none yet"
                    [ SlashCommand.courseOption () ]
                subcommand "leave" "Leave a course" [ SlashCommand.courseOption () ]
                subcommand "leave-all" "Leave all your courses" []
                subcommand "mine" "Your courses" []
                subcommand "info" "A course's channel and members" [ SlashCommand.courseOption () ]
            ]
        Permission = None
        Handle = handle
    }

let onButton
    (repository: CourseRepository)
    (guild: unit -> IGuild)
    (settings: unit -> CourseSettings)
    (queue: SlashCommand.Queue)
    (interaction: SocketMessageComponent)
    : Task =
    match Membership.parseButton interaction.Data.CustomId with
    | Some courseId ->
        let user = box interaction.User :?> IGuildUser

        SlashCommand.reply queue true interaction $"Course request button by {user.Id}" (fun () ->
            task {
                match! repository.FindById courseId with
                | Some course -> return! Membership.toggle repository (settings ()) (guild ()) course user
                | None -> return "That course is no longer in the catalog."
            })
    | None -> Task.CompletedTask
