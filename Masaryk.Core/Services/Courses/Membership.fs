/// Members joining and leaving courses, interest requests, and opening a course's channel.
module Masaryk.Core.Services.Courses.Membership

open System
open System.Threading.Tasks
open Discord
open Discord.Net
open Masaryk.Configuration
open Masaryk.Core.Services.Shared
open Masaryk.IS
open Masaryk.Postgres

let label (course: Course) = $"{course.Faculty}:{course.Code}"

/// Autocomplete values are `FACULTY:CODE`. A bare code means the home faculty.
let find (repository: CourseRepository) (home: string option) (value: string) : Task<Course option> =
    match value.Split(':', 2) with
    | [| faculty; code |] -> repository.Find(faculty.Trim(), code.Trim())
    | _ ->
        home
        |> Option.map (fun home -> repository.Find(home, value.Trim()))
        |> Option.defaultValue (Task.FromResult None)

let private refused (code: DiscordErrorCode) (error: HttpException) = error.DiscordCode = Nullable code

let private view =
    OverwritePermissions.InheritAll.Modify(viewChannel = PermValue.Allow)

/// Named like the course's channel, with no faculty prefix for home faculty courses.
let private roleName (settings: CourseSettings) (course: Course) =
    if Some course.Faculty = settings.HomeFaculty then
        $"📖{course.Code}"
    else
        $"📖{label course}"

let private courseRole (settings: CourseSettings) (guild: IGuild) (course: Course) =
    guild.Roles |> Seq.tryFind (fun r -> r.Name = roleName settings course)

/// Gives the member their own overwrite on the channel. When the channel can't take more overwrites,
/// gives them a role for the course instead. False when no more roles can be created either.
let private grant
    (settings: CourseSettings)
    (guild: IGuild)
    (channel: IGuildChannel)
    (course: Course)
    (user: IGuildUser)
    : Task<bool> =
    task {
        match courseRole settings guild course with
        | Some role ->
            do! user.AddRoleAsync role
            return true
        | None ->
            try
                do! channel.AddPermissionOverwriteAsync(user, view)
                return true
            with :? HttpException as error when
                refused DiscordErrorCode.MaximumNumberOfChannelPermissionOverwritesReached error ->
                try
                    let! role =
                        guild.CreateRoleAsync(
                            roleName settings course,
                            Nullable GuildPermissions.None,
                            isMentionable = false
                        )

                    do! channel.AddPermissionOverwriteAsync(role, view)
                    do! user.AddRoleAsync role
                    return true
                with :? HttpException as error when refused DiscordErrorCode.MaximumGuildRolesReached error ->
                    return false
    }

/// Also works for someone who left the server, whose roles went with them.
let private revoke
    (settings: CourseSettings)
    (guild: IGuild)
    (channel: IGuildChannel)
    (course: Course)
    (user: IUser)
    : Task =
    task {
        if (channel.GetPermissionOverwrite user).HasValue then
            do! channel.RemovePermissionOverwriteAsync user

        match user, courseRole settings guild course with
        | :? IGuildUser as member', Some role when Seq.contains role.Id member'.RoleIds ->
            do! member'.RemoveRoleAsync role
        | _ -> ()
    }

/// The course's channel, if it has one that still exists.
let private channelOf (repository: CourseRepository) (guild: IGuild) (course: Course) : Task<IGuildChannel option> =
    task {
        match! repository.ChannelOf course.Id with
        | Some id ->
            let! channel = guild.GetChannelAsync id
            return Option.ofObj channel
        | None -> return None
    }

let private buttonPrefix = "course-interest:"

let parseButton (customId: string) : int64 option =
    if customId.StartsWith buttonPrefix then
        match Int64.TryParse(customId.Substring buttonPrefix.Length) with
        | true, id -> Some id
        | _ -> None
    else
        None

let private title (course: Course) =
    MessageEmbed.shorten 256 $"{label course} {course.Name}"

let private requestEmbed (settings: CourseSettings) (course: Course) (count: int) =
    let embed =
        EmbedBuilder()
            .WithTitle(title course)
            .WithDescription(
                $"**{count}/{settings.MinimumMembers}** interested. The channel opens at {settings.MinimumMembers}."
            )
            .WithFooter("Click again to take it back.")

    match Faculties.coursePage course.Faculty course.Code with
    | Some page -> embed.WithUrl(page).Build()
    | None -> embed.Build()

let private interestButton (course: Course) =
    ComponentBuilder().WithButton("I'm interested", $"{buttonPrefix}{course.Id}", ButtonStyle.Primary).Build()

let private openedEmbed (course: Course) (channelId: uint64) =
    EmbedBuilder()
        .WithTitle(title course)
        .WithDescription($"Channel created: <#{channelId}>")
        .WithColor(Color.Green)
        .Build()

/// The course's request message, if it still exists.
let private requestMessage (repository: CourseRepository) (guild: IGuild) (course: Course) : Task<IUserMessage option> =
    task {
        match! repository.Request course.Id with
        | Some(channelId, messageId) ->
            match! guild.GetTextChannelAsync channelId with
            | null -> return None
            | channel ->
                let! message = channel.GetMessageAsync messageId
                return tryUnbox<IUserMessage> message
        | None -> return None
    }

/// Post the course's request in the interest channel, or update its count. Returns its link.
let private showRequest
    (repository: CourseRepository)
    (settings: CourseSettings)
    (guild: IGuild)
    (course: Course)
    (count: int)
    : Task<string> =
    task {
        let embed = requestEmbed settings course count

        match! requestMessage repository guild course with
        | Some message ->
            do!
                message.ModifyAsync(fun properties ->
                    properties.Embed <- embed
                    properties.Components <- interestButton course)

            return message.GetJumpUrl()
        | None ->
            let! channel =
                match settings.InterestChannel with
                | Some id -> guild.GetTextChannelAsync id
                | None -> Task.FromResult null

            match channel with
            | null -> return failwith "The interest channel is gone. An admin has to set courses.interestChannel again."
            | channel ->
                let! message =
                    channel.SendMessageAsync(embed = embed, components = interestButton course)

                do! repository.SaveRequest(course.Id, channel.Id, message.Id)
                return message.GetJumpUrl()
    }

let private dropRequest (repository: CourseRepository) (guild: IGuild) (course: Course) : Task =
    task {
        match! requestMessage repository guild course with
        | Some message -> do! message.DeleteAsync()
        | None -> ()

        do! repository.RemoveRequest course.Id
    }

let private tell (guild: IGuild) (course: Course) (channelId: uint64) (user: IGuildUser) : Task =
    task {
        try
            let! dm = user.CreateDMChannelAsync()

            let! _ =
                dm.SendMessageAsync(
                    $"The channel for **{label course}** {course.Name} on {guild.Name} is open: https://discord.com/channels/{guild.Id}/{channelId}"
                )

            ()
        // Members with closed DMs see the opened request in the interest channel instead.
        with :? HttpException as error when refused DiscordErrorCode.CannotSendMessageToUser error ->
            ()
    }

/// Let the members waiting for the course into its channel, tell them, and turn the request into
/// a note that the channel opened. Nothing happens when nobody was waiting.
let admit
    (repository: CourseRepository)
    (settings: CourseSettings)
    (guild: IGuild)
    (course: Course)
    (channel: IGuildChannel)
    : Task =
    task {
        match! repository.Request course.Id with
        | None -> ()
        | Some _ ->
            let! members = repository.Members course.Id

            for id in members do
                match! guild.GetUserAsync id with
                | null -> ()
                | user ->
                    let! _ = grant settings guild channel course user
                    do! tell guild course channel.Id user

            match! requestMessage repository guild course with
            | Some message ->
                do!
                    message.ModifyAsync(fun properties ->
                        properties.Embed <- openedEmbed course channel.Id
                        properties.Components <- ComponentBuilder().Build())
            | None -> ()

            do! repository.RemoveRequest course.Id
    }

/// Create the course's private channel and let in the members waiting for it.
let openChannel
    (repository: CourseRepository)
    (settings: CourseSettings)
    (guild: IGuild)
    (course: Course)
    : Task<Guild.Creation> =
    task {
        let! creation = Guild.create repository settings guild course

        match creation with
        | Guild.Created channel -> do! admit repository settings guild course channel
        | _ -> ()

        return creation
    }

let join
    (repository: CourseRepository)
    (settings: CourseSettings)
    (guild: IGuild)
    (course: Course)
    (user: IGuildUser)
    : Task<string> =
    task {
        match! channelOf repository guild course with
        | Some channel ->
            let! _ = repository.AddMember(course.Id, user.Id)

            match! grant settings guild channel course user with
            | true -> return $"You can see <#{channel.Id}> now."
            | false ->
                return
                    $"I can't let you into <#{channel.Id}>: Discord won't take more overwrites there or more roles on the server. Ask an admin."
        | None when settings.InterestChannel.IsNone ->
            return $"{label course} has no channel yet. Ask an admin to create one."
        | None ->
            let! _ = repository.AddMember(course.Id, user.Id)
            let! members = repository.Members course.Id

            if members.Length >= settings.MinimumMembers then
                match! openChannel repository settings guild course with
                | Guild.Created channel -> return $"That made {members.Length}, so the channel is open: <#{channel.Id}>"
                | Guild.Exists id -> return $"That made {members.Length}, so the channel is open: <#{id}>"
                | Guild.Full ->
                    let! link = showRequest repository settings guild course members.Length

                    return
                        $"{members.Length} are interested, but the server has no room for another channel. An admin has to make room. {link}"
            else
                let! link = showRequest repository settings guild course members.Length

                return
                    $"You're interested in {label course} ({members.Length}/{settings.MinimumMembers}). The channel opens at {settings.MinimumMembers}: {link}"
    }

let leave
    (repository: CourseRepository)
    (settings: CourseSettings)
    (guild: IGuild)
    (course: Course)
    (user: IUser)
    : Task<string> =
    task {
        let! wasMember = repository.RemoveMember(course.Id, user.Id)

        match! channelOf repository guild course with
        | Some channel ->
            // Also for members without a row, such as those let in before /course.
            do! revoke settings guild channel course user
            return $"You left {label course}."
        | None when wasMember ->
            let! members = repository.Members course.Id

            if members.IsEmpty then
                do! dropRequest repository guild course
            else
                let! _ = showRequest repository settings guild course members.Length
                ()

            return $"You're no longer interested in {label course}."
        | None -> return $"You weren't in {label course}."
    }

/// The request button joins, or takes the interest back when already interested.
let toggle
    (repository: CourseRepository)
    (settings: CourseSettings)
    (guild: IGuild)
    (course: Course)
    (user: IGuildUser)
    : Task<string> =
    task {
        let! members = repository.Members course.Id

        if List.contains user.Id members then
            return! leave repository settings guild course user
        else
            return! join repository settings guild course user
    }

let leaveAll (repository: CourseRepository) (settings: CourseSettings) (guild: IGuild) (user: IUser) : Task<string> =
    task {
        let! courses = repository.CoursesOf user.Id

        for course, _, _ in courses do
            let! _ = leave repository settings guild course user
            ()

        return $"You left {courses.Length} courses."
    }

let mine (repository: CourseRepository) (settings: CourseSettings) (user: IGuildUser) : Task<string> =
    task {
        match! repository.CoursesOf user.Id with
        | [] -> return "You haven't joined any courses. Use `/course join`."
        | courses ->
            return
                courses
                |> List.map (fun (course, channel, count) ->
                    match channel with
                    | Some id -> $"**{label course}** {course.Name} · <#{id}>"
                    | None -> $"**{label course}** {course.Name} · waiting ({count}/{settings.MinimumMembers})")
                |> String.concat "\n"
                |> MessageEmbed.shorten 2000
    }

let info (repository: CourseRepository) (settings: CourseSettings) (guild: IGuild) (course: Course) : Task<string> =
    task {
        let! members = repository.Members course.Id
        let! channel = channelOf repository guild course

        let state =
            match channel with
            | Some channel when members.Length = 1 -> $"<#{channel.Id}>, 1 member"
            | Some channel -> $"<#{channel.Id}>, {members.Length} members"
            | None -> $"No channel yet, {members.Length}/{settings.MinimumMembers} interested"

        let page =
            Faculties.coursePage course.Faculty course.Code
            |> Option.map (fun page -> $"\n{page}")
            |> Option.defaultValue ""

        return $"**{label course}** {course.Name}{page}\n{state}"
    }

/// Rebuild members of linked channels from their own View Channel overwrites. Returns how many
/// channels had any and how many members were new.
let recover (repository: CourseRepository) (guild: IGuild) : Task<int * int> =
    task {
        let! links = repository.Links()
        let! bot = guild.GetCurrentUserAsync()
        let channels = ref 0
        let added = ref 0

        for courseId, channelId in links do
            match! guild.GetChannelAsync channelId with
            | null -> ()
            | channel ->
                let users =
                    channel.PermissionOverwrites
                    |> Seq.filter (fun o ->
                        o.TargetType = PermissionTarget.User
                        && o.TargetId <> bot.Id
                        && o.Permissions.ViewChannel = PermValue.Allow)
                    |> Seq.map _.TargetId
                    |> List.ofSeq

                if not users.IsEmpty then
                    let! count = repository.AddMembers(courseId, users)
                    channels.Value <- channels.Value + 1
                    added.Value <- added.Value + count

        return channels.Value, added.Value
    }
