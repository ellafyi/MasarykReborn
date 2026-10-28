/// Takes course access away from members who left the server or haven't written for a long time.
module Masaryk.Core.Services.Courses.Prune

open System
open System.Threading.Tasks
open Discord
open Discord.Net
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Core.Services
open Masaryk.Postgres

/// Removals per run. A big backlog takes several days instead of tying up the course queue.
let private cap = 200

type private Plan =
    {
        /// Members who left, with their courses. Empty until the member list has loaded.
        Departed: (uint64 * int64 list) list
        /// Silent members with their courses.
        Inactive: (uint64 * int64 list) list
        /// Whether the member list had loaded, so members who left could be told apart.
        Complete: bool
    }

let private byMember (memberships: (int64 * uint64) list) =
    memberships
    |> List.groupBy snd
    |> List.map (fun (user, rows) -> user, List.map fst rows)

let private memberships (n: int) =
    if n = 1 then "1 membership" else $"{n} memberships"

let private plan (repository: CourseRepository) (settings: CourseSettings) (guild: SocketGuild) =
    task {
        let! memberships = repository.Memberships()

        let departed =
            if guild.HasAllMembers then
                memberships
                |> List.filter (fun (_, user) -> isNull (guild.GetUser user))
                |> byMember
            else
                []

        let gone = departed |> List.map fst |> Set.ofList

        let! inactive =
            match settings.InactiveDays with
            | Some days -> repository.Inactive days
            | None -> Task.FromResult []

        return
            {
                Departed = departed
                Inactive = inactive |> List.filter (snd >> gone.Contains >> not) |> byMember
                Complete = guild.HasAllMembers
            }
    }

let private count (members: (uint64 * int64 list) list) =
    members |> List.sumBy (snd >> List.length)

let private tell (guild: IGuild) (user: IUser) (days: int) (courses: string list) : Task =
    task {
        try
            let! dm = user.CreateDMChannelAsync()
            let list = String.concat ", " courses

            let! _ =
                dm.SendMessageAsync(
                    $"You haven't written on {guild.Name} for {days} days, so I took you out of {list}. `/course join` brings you back."
                )

            ()
        with :? HttpException as error when error.DiscordCode = Nullable DiscordErrorCode.CannotSendMessageToUser ->
            ()
    }

/// Remove up to `cap` memberships, members who left first. Returns what it did as one line.
let private run
    (repository: CourseRepository)
    (settings: CourseSettings)
    (client: DiscordSocketClient)
    (guild: SocketGuild)
    (plan: Plan)
    : Task<string> =
    task {
        let budget = ref cap

        let take (courses: int64 list) =
            let taken = List.truncate budget.Value courses
            budget.Value <- budget.Value - taken.Length
            taken

        // The member from the cache, else from Discord. Null for a deleted account.
        let find (userId: uint64) : Task<IUser> =
            task {
                match guild.GetUser userId with
                | null ->
                    let! user = client.Rest.GetUserAsync userId
                    return user :> IUser
                | user -> return user :> IUser
            }

        let remove (userId: uint64) (user: IUser) (courses: int64 list) =
            task {
                let left = ResizeArray<string>()

                for courseId in take courses do
                    match! repository.FindById courseId with
                    | Some course when isNull user ->
                        let! _ = repository.RemoveMember(course.Id, userId)
                        left.Add(Membership.label course)
                    | Some course ->
                        let! _ = Membership.leave repository settings guild course user
                        left.Add(Membership.label course)
                    | None -> ()

                return List.ofSeq left
            }

        let departed = ref 0
        let silent = ref 0

        for userId, courses in plan.Departed do
            if budget.Value > 0 then
                let! user = find userId
                let! left = remove userId user courses
                departed.Value <- departed.Value + left.Length

        for userId, courses in plan.Inactive do
            if budget.Value > 0 then
                let! user = find userId
                let! left = remove userId user courses
                silent.Value <- silent.Value + left.Length

                if not left.IsEmpty && not (isNull user) then
                    do! tell guild user settings.InactiveDays.Value left

        let rest = count plan.Departed + count plan.Inactive - departed.Value - silent.Value
        let later = if rest > 0 then $" {rest} more next time." else ""

        return
            $"Course cleanup: removed {memberships departed.Value} of members who left and {memberships silent.Value} of silent members.{later}"
    }

let private report (reviewed: bool) (settings: CourseSettings) (plan: Plan) : string =
    let mentions (members: (uint64 * int64 list) list) =
        let shown =
            members
            |> List.truncate 20
            |> List.map (fun (user, _) -> $"<@{user}>")
            |> String.concat " "

        if members.Length > 20 then
            $"{shown} and {members.Length - 20} more"
        else
            shown

    let state =
        match settings.InactiveDays with
        | None -> "off, `courses.inactiveDays` is unset"
        | Some _ when not reviewed -> "off until an admin looks at this report, which is now"
        | Some _ -> "on, once a day"

    [
        $"**Course cleanup** (automatic: {state})"
        if not plan.Complete then
            "Members who left: unknown until the member list has loaded."
        elif plan.Departed.IsEmpty then
            "Members who left: none."
        else
            $"Members who left: {plan.Departed.Length} with {memberships (count plan.Departed)}: {mentions plan.Departed}"
        match settings.InactiveDays with
        | None -> ()
        | Some days when plan.Inactive.IsEmpty -> $"Silent for {days} days: none."
        | Some days ->
            $"Silent for {days} days: {plan.Inactive.Length} with {memberships (count plan.Inactive)}: {mentions plan.Inactive}"
        $"A run removes at most {cap} memberships. Use `now: True` to run it right away."
    ]
    |> String.concat "\n"

/// `/courses prune` reports, or runs the cleanup with `now`. Either way an admin has now seen it.
let command
    (repository: CourseRepository)
    (settings: CourseSettings)
    (client: DiscordSocketClient)
    (guild: SocketGuild)
    (now: bool)
    : Task<string> =
    task {
        let! reviewed = repository.PruneReviewed()
        do! repository.MarkPruneReviewed()
        let! plan = plan repository settings guild

        if now then
            return! run repository settings client guild plan
        else
            return report reviewed settings plan
    }

/// The daily run. It waits until an admin has looked at `/courses prune`.
let daily (context: Context) (repository: CourseRepository) (settings: CourseSettings) (guild: SocketGuild) : Task =
    task {
        match! repository.PruneReviewed() with
        | false -> ()
        | true ->
            let! plan = plan repository settings guild

            if count plan.Departed + count plan.Inactive > 0 then
                let! text = run repository settings context.Client guild plan
                printfn $"{text}"

                match Config.logChannel context with
                | Some channel -> do! channel.SendMessageAsync(text, allowedMentions = AllowedMentions.None) :> Task
                | None -> ()
    }
