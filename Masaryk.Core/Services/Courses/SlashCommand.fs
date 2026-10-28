module Masaryk.Core.Services.Courses.SlashCommand

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Core.Options
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let private name = "courses"

/// Runs course changes one at a time, so two clicks can't open a channel twice.
type Queue = string -> (unit -> Task) -> unit

let reply
    (queue: Queue)
    (ephemeral: bool)
    (interaction: IDiscordInteraction)
    (describe: string)
    (work: unit -> Task<string>)
    : Task =
    task {
        do! interaction.DeferAsync(ephemeral)

        queue describe (fun () ->
            task {
                let! text =
                    task {
                        try
                            return! work ()
                        with error ->
                            eprintfn $"{describe} failed: {error}"
                            return "Something went wrong. An admin can see why in my log."
                    }

                do!
                    interaction.FollowupAsync(text, ephemeral = ephemeral, allowedMentions = AllowedMentions.None)
                    :> Task
            })
    }

let autocomplete
    (repository: CourseRepository)
    (home: unit -> string option)
    (interaction: SocketAutocompleteInteraction)
    : Task =
    task {
        if interaction.Data.CommandName = name || interaction.Data.CommandName = "course" then
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

let courseOption () =
    (option "course" "Course code, such as IB002" ApplicationCommandOptionType.String)
        .WithRequired(true)
        .WithAutocomplete(true)

let command
    (repository: CourseRepository)
    (guild: unit -> IGuild)
    (settings: unit -> CourseSettings)
    (queue: Queue)
    : Command =
    let handle (interaction: SocketSlashCommand) : Task =
        task {
            let subcommand, value = input interaction
            let settings = settings ()
            let guild = guild ()
            let answer = reply queue false interaction $"/courses {subcommand}"

            let withCourse (act: Course -> Task<string>) =
                answer (fun () ->
                    task {
                        let code = string (value "course").Value

                        match! Membership.find repository settings.HomeFaculty code with
                        | Some course -> return! act course
                        | None -> return $"There's no course {code} in the catalog."
                    })

            match subcommand with
            | "list" ->
                let! embed, buttons = page repository (value "faculty" |> Option.map string) 0
                do! interaction.RespondAsync(embed = embed, components = buttons, ephemeral = true)
            | "match" ->
                do!
                    answer (fun () ->
                        task {
                            let! matching = Guild.matchChannels repository settings.HomeFaculty guild

                            for course, channelId in matching.Links do
                                let! channel = guild.GetChannelAsync channelId
                                do! Membership.admit repository settings guild course channel

                            return report matching
                        })
            | "recover" ->
                do!
                    answer (fun () ->
                        task {
                            let! channels, added = Membership.recover repository guild
                            return $"Found members in {channels} linked channels, {added} of them new."
                        })
            | "link" ->
                let channel = (value "channel").Value :?> IGuildChannel

                do!
                    withCourse (fun course ->
                        task {
                            let! moved = Guild.link repository settings.HomeFaculty guild course channel.Id
                            do! Membership.admit repository settings guild course channel

                            match moved with
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
                            match! Membership.openChannel repository settings guild course with
                            | Guild.Created channel -> return $"Created <#{channel.Id}>."
                            | Guild.Exists id -> return $"{course.Code} already has <#{id}>."
                            | Guild.Full -> return "The server has no room for another channel."
                        })
        }

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
                subcommand "recover" "Add members of linked channels from their own channel permissions" []
                subcommand
                    "link"
                    "Link a course to an existing channel"
                    [ courseOption (); textChannel "channel" "The course's channel" ]
                subcommand "unlink" "Forget a course's channel" [ courseOption () ]
                subcommand "create" "Create a private channel for a course" [ courseOption () ]
            ]
        Permission = Some GuildPermission.Administrator
        Handle = handle
    }
