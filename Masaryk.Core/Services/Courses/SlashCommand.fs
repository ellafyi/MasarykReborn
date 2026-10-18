module Masaryk.Core.Services.Courses.SlashCommand

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core
open Masaryk.Postgres

let private name = "courses"

/// Autocomplete values are `FACULTY:CODE`; a bare code means the home faculty.
let private find (repository: CourseRepository) (value: string) =
    match value.Split(':', 2) with
    | [| faculty; code |] -> repository.Find(faculty.Trim(), code.Trim())
    | _ -> repository.Find(Channels.home, value.Trim())

let private cut (length: int) (text: string) =
    if text.Length > length then
        text[.. length - 2] + "…"
    else
        text

let autocomplete (repository: CourseRepository) (interaction: SocketAutocompleteInteraction) : Task =
    task {
        if interaction.Data.CommandName = name then
            let! courses =
                repository.Search(string interaction.Data.Current.Value, Channels.home)

            let results =
                courses
                |> List.map (fun c ->
                    AutocompleteResult(cut 100 $"{c.Faculty}:{c.Code} {c.Name}", $"{c.Faculty}:{c.Code}"))

            do! interaction.RespondAsync(results)
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
    |> cut 2000

let command (repository: CourseRepository) (guild: unit -> IGuild) : Command =
    let handle (interaction: SocketSlashCommand) : Task =
        task {
            let subcommand = Seq.head interaction.Data.Options

            let option name =
                subcommand.Options |> Seq.tryFind (fun o -> o.Name = name) |> Option.map _.Value

            let withCourse (act: Course -> Task) =
                task {
                    let value = option "course" |> Option.map string |> Option.defaultValue ""

                    match! find repository value with
                    | Some course -> do! act course
                    | None -> do! interaction.FollowupAsync($"There's no course {value} in the catalog.") :> Task
                }

            do! interaction.DeferAsync()

            match subcommand.Name with
            | "match" ->
                let! matching = Guild.matchChannels repository (guild ())
                let! _ = interaction.FollowupAsync(report matching)
                ()
            | "link" ->
                let channel = (option "channel").Value :?> IChannel

                do!
                    withCourse (fun course ->
                        task {
                            do! repository.Link(course.Id, channel.Id)
                            let! _ = interaction.FollowupAsync($"Linked {course.Code} to <#{channel.Id}>.")
                            ()
                        })
            | "unlink" ->
                do!
                    withCourse (fun course ->
                        task {
                            let! removed = repository.Unlink course.Id

                            let text =
                                if removed then
                                    $"{course.Code} is no longer linked to a channel."
                                else
                                    $"{course.Code} wasn't linked to a channel."

                            let! _ = interaction.FollowupAsync(text)
                            ()
                        })
            | _ ->
                do!
                    withCourse (fun course ->
                        task {
                            let! text =
                                task {
                                    match! Guild.create repository (guild ()) course with
                                    | Guild.Created channel -> return $"Created <#{channel.Id}>."
                                    | Guild.Exists id -> return $"{course.Code} already has <#{id}>."
                                }

                            let! _ = interaction.FollowupAsync(text)
                            ()
                        })
        }

    let course () =
        SlashCommandOptionBuilder()
            .WithName("course")
            .WithDescription("Course code, such as IB002")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .WithAutocomplete(true)

    let subcommand (name: string) (description: string) (options: SlashCommandOptionBuilder list) =
        SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOptions(List.toArray options)

    {
        Name = name
        Description = "Course channels"
        Options =
            [
                subcommand "match" "Link channels named after courses in the catalog" []
                subcommand
                    "link"
                    "Link a course to an existing channel"
                    [
                        course ()
                        SlashCommandOptionBuilder()
                            .WithName("channel")
                            .WithDescription("The course's channel")
                            .WithType(ApplicationCommandOptionType.Channel)
                            .AddChannelType(ChannelType.Text)
                            .WithRequired(true)
                    ]
                subcommand "unlink" "Forget a course's channel" [ course () ]
                subcommand "create" "Create a channel for a course" [ course () ]
            ]
        Permission = Some GuildPermission.ManageChannels
        Handle = handle
    }
