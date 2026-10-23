module Masaryk.Core.Services.Courses.SlashCommand

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Core.Options
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let private name = "courses"

/// Autocomplete values are `FACULTY:CODE`; a bare code means the home faculty.
let private find (repository: CourseRepository) (home: string option) (value: string) =
    match value.Split(':', 2) with
    | [| faculty; code |] -> repository.Find(faculty.Trim(), code.Trim())
    | _ ->
        home
        |> Option.map (fun home -> repository.Find(home, value.Trim()))
        |> Option.defaultValue (Task.FromResult None)

let autocomplete
    (repository: CourseRepository)
    (home: unit -> string option)
    (interaction: SocketAutocompleteInteraction)
    : Task =
    task {
        if interaction.Data.CommandName = name then
            let typed = string interaction.Data.Current.Value

            if interaction.Data.Current.Name = "faculty" then
                let! faculties = repository.Faculties()

                do!
                    interaction.RespondAsync(
                        faculties
                        |> List.filter _.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                        |> List.map (fun f -> AutocompleteResult(f, f))
                    )
            else
                let! courses = repository.Search(typed, home ())

                do!
                    interaction.RespondAsync(
                        courses
                        |> List.map (fun c ->
                            AutocompleteResult(
                                MessageEmbed.shorten 100 $"{c.Faculty}:{c.Code} {c.Name}",
                                $"{c.Faculty}:{c.Code}"
                            ))
                    )
    }

let private page (repository: CourseRepository) (faculty: string option) (number: int) =
    task {
        let! courses, total =
            repository.Page(faculty, number * Listing.pageSize, Listing.pageSize)

        return Listing.render faculty number courses total
    }

let turnPage (repository: CourseRepository) (interaction: SocketMessageComponent) : Task =
    task {
        match Pager.parse Listing.prefix interaction.Data.CustomId with
        | Some(faculty, number) ->
            let faculty = if faculty = "" then None else Some faculty
            let! embed, buttons = page repository faculty number

            do!
                interaction.UpdateAsync(fun message ->
                    message.Embed <- embed
                    message.Components <- buttons)
        | None -> ()
    }

let private report (matching: Channels.Matching) =
    let list (items: string list) =
        let shown = items |> List.truncate 30 |> String.concat ", "

        if items.Length > 30 then
            $"{shown} and {items.Length - 30} more"
        else
            shown

    [
        $"Linked {matching.Links.Length} channels."
        if not matching.Contested.IsEmpty then
            let contested =
                matching.Contested
                |> List.map (fun (course, channels) -> $"""{course.Code} ({String.Join(", ", channels)})""")

            $"Several channels for one course, link one with `/courses link`: {list contested}"
        if not matching.Unknown.IsEmpty then
            $"Not in the catalog: {list matching.Unknown}"
    ]
    |> String.concat "\n\n"
    |> MessageEmbed.shorten 2000

let command (repository: CourseRepository) (guild: unit -> IGuild) (home: unit -> string option) : Command =
    let handle (interaction: SocketSlashCommand) : Task =
        task {
            let subcommand, value = input interaction
            let home = home ()

            let withCourse (answer: Course -> Task<string>) =
                task {
                    do! interaction.DeferAsync()
                    let code = string (value "course").Value
                    let! course = find repository home code

                    let! text =
                        match course with
                        | Some course -> answer course
                        | None -> Task.FromResult $"There's no course {code} in the catalog."

                    do! interaction.FollowupAsync(text) :> Task
                }

            match subcommand with
            | "list" ->
                let! embed, buttons = page repository (value "faculty" |> Option.map string) 0
                do! interaction.RespondAsync(embed = embed, components = buttons, ephemeral = true)
            | "match" ->
                do! interaction.DeferAsync()
                let! matching = Guild.matchChannels repository home (guild ())
                do! interaction.FollowupAsync(report matching) :> Task
            | "link" ->
                let channel = (value "channel").Value :?> IChannel

                do!
                    withCourse (fun course ->
                        task {
                            match! Guild.link repository home (guild ()) course channel.Id with
                            | Some category ->
                                return $"Linked {course.Code} to <#{channel.Id}> and moved it to {category.Name}."
                            | None -> return $"Linked {course.Code} to <#{channel.Id}>."
                        })
            | "unlink" ->
                do!
                    withCourse (fun course ->
                        task {
                            match! repository.Unlink course.Id with
                            | true -> return $"{course.Code} is no longer linked to a channel."
                            | false -> return $"{course.Code} wasn't linked to a channel."
                        })
            | _ ->
                do!
                    withCourse (fun course ->
                        task {
                            match! Guild.create repository home (guild ()) course with
                            | Guild.Created id -> return $"Created <#{id}>."
                            | Guild.Exists id -> return $"{course.Code} already has <#{id}>."
                        })
        }

    let course () =
        (option "course" "Course code, such as IB002" ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(true)

    {
        Name = name
        Description = "Course channels"
        Options =
            [
                subcommand
                    "list"
                    "Courses in the catalog"
                    [
                        (option "faculty" "Only this faculty's courses, such as FI" ApplicationCommandOptionType.String)
                            .WithAutocomplete(true)
                    ]
                subcommand "match" "Link channels named after courses in the catalog" []
                subcommand
                    "link"
                    "Link a course to an existing channel"
                    [ course (); textChannel "channel" "The course's channel" ]
                subcommand "unlink" "Forget a course's channel" [ course () ]
                subcommand "create" "Create a channel for a course" [ course () ]
            ]
        Permission = Some GuildPermission.Administrator
        Handle = handle
    }
