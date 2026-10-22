/// `/touch-grass` with its confirmation buttons, and `/touch-grass-check`.
module Masaryk.Core.Services.Lockout.SlashCommand

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Core.Options
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

/// Presets rather than free input: the lockout can't be undone, so a typo shouldn't cost a week.
let private durations =
    [
        "1 hour", 1
        "2 hours", 2
        "3 hours", 3
        "6 hours", 6
        "12 hours", 12
        "1 day", 24
        "2 days", 48
        "3 days", 72
        "5 days", 120
        "1 week", 168
    ]

let private confirmId (user: uint64) (hours: int) = $"touch-grass:confirm:{user}:{hours}"

let private cancelId = "touch-grass:cancel"

let private noButtons = ComponentBuilder().Build()

let private tag (time: DateTimeOffset) (style: TimestampTagStyles) =
    TimestampTag.FromDateTimeOffset(time, style)

let private lockedText (locked: LockedMember) =
    $"Locked out until {tag locked.Until TimestampTagStyles.LongDateTime} ({tag locked.Until TimestampTagStyles.Relative}). Your roles come back on their own."

/// Everything that must hold before someone is locked out, checked again when they confirm.
let private check
    (context: Context)
    (hidden: LockoutSettings option ref)
    (repository: LockoutRepository)
    (user: IUser)
    (hours: int)
    : Task<Result<LockoutSettings * IGuildUser, string>> =
    task {
        let guild = context.Client.GetGuild context.GuildId

        match context.Configuration.Current.Lockout, user with
        | _ when not (durations |> List.exists (snd >> (=) hours)) -> return Error "That isn't one of the choices."
        | None, _ -> return Error "/touch-grass isn't set up on this server."
        | Some settings, (:? IGuildUser as target) when hidden.Value = Some settings ->
            match Members.obstacle guild settings.RoleId target with
            | Some problem -> return Error problem
            | None ->
                match! repository.Find target.Id with
                | Some locked ->
                    return Error $"You're already locked out until {tag locked.Until TimestampTagStyles.LongDateTime}."
                | None -> return Ok(settings, target)
        | Some _, (:? IGuildUser) ->
            return
                Error
                    "I'm still hiding the channels from the lockout role, or I can't (an admin can see why in my log). Try again in a minute."
        | Some _, _ -> return Error "Use this in the server."
    }

let private ask
    (context: Context)
    (hidden: LockoutSettings option ref)
    (repository: LockoutRepository)
    (interaction: SocketSlashCommand)
    : Task =
    task {
        let _, value = input interaction
        let hours = int (unbox<int64> (value "duration").Value)

        match! check context hidden repository interaction.User hours with
        | Error problem -> do! interaction.RespondAsync(problem, ephemeral = true)
        | Ok(settings, target) ->
            let label = durations |> List.find (snd >> (=) hours) |> fst
            let until = DateTimeOffset.UtcNow.AddHours(float hours)

            let text =
                $"For **{label}** (until {tag until TimestampTagStyles.LongDateTime}, {tag until TimestampTagStyles.Relative}) I'll take away all your roles and hide every channel except <#{settings.ChannelId}>. **You can't undo this yourself.** Your roles come back on their own when the time is up. Lock yourself out?"

            let buttons =
                ComponentBuilder()
                    .WithButton("Lock me out", confirmId target.Id hours, ButtonStyle.Danger)
                    .WithButton("Cancel", cancelId, ButtonStyle.Secondary)
                    .Build()

            do! interaction.RespondAsync(text, components = buttons, ephemeral = true)
    }

/// The role change failed or may have failed. Keep the saved row unless the member surely kept
/// their roles, since the row is what brings them back.
let private failed
    (context: Context)
    (repository: LockoutRepository)
    (locked: LockedMember)
    (error: exn)
    : Task<string> =
    task {
        eprintfn $"Touch grass: couldn't lock out {locked.UserId}: {error.Message}"

        try
            let! current = context.Client.Rest.GetGuildUserAsync(context.GuildId, locked.UserId)

            if not (isNull current) && Seq.contains locked.LockRole current.RoleIds then
                return lockedText locked
            else
                do! repository.Remove locked.UserId
                return "I couldn't change your roles, so nothing happened. An admin should check my permissions."
        with check ->
            eprintfn $"Touch grass: can't tell whether {locked.UserId} is locked out: {check.Message}"

            return
                "Something went wrong and I can't tell whether you're locked out. If you are, your roles come back at the end."
    }

let private lockOut
    (context: Context)
    (hidden: LockoutSettings option ref)
    (repository: LockoutRepository)
    (interaction: SocketMessageComponent)
    (hours: int)
    : Task =
    task {
        do! interaction.DeferAsync()

        let! text =
            task {
                match! check context hidden repository interaction.User hours with
                | Error problem -> return problem
                | Ok(settings, target) ->
                    let guild = context.Client.GetGuild context.GuildId
                    let length = TimeSpan.FromHours(float hours)

                    match! repository.Start(target.Id, settings.RoleId, Members.taken guild target, length) with
                    | None -> return "You're already locked out."
                    | Some locked ->
                        try
                            do! Members.lock guild settings.RoleId target
                            return lockedText locked
                        with error ->
                            return! failed context repository locked error
            }

        let! _ =
            interaction.ModifyOriginalResponseAsync(fun message ->
                message.Content <- text
                message.Components <- noButtons)

        ()
    }

let onButton
    (context: Context)
    (hidden: LockoutSettings option ref)
    (repository: LockoutRepository)
    (interaction: SocketMessageComponent)
    : Task =
    match interaction.Data.CustomId.Split ':' with
    | [| "touch-grass"; "confirm"; user; hours |] when user = string interaction.User.Id ->
        lockOut context hidden repository interaction (int hours)
    | [| "touch-grass"; "cancel" |] ->
        interaction.UpdateAsync(fun message ->
            message.Content <- "Cancelled."
            message.Components <- noButtons)
    | _ -> Task.CompletedTask

/// `/touch-grass-check`: a private report for admins.
let private inspect
    (context: Context)
    (hidden: LockoutSettings option ref)
    (repository: LockoutRepository)
    (interaction: SocketSlashCommand)
    : Task =
    task {
        do! interaction.DeferAsync(ephemeral = true)

        let settings = context.Configuration.Current.Lockout
        let! active = repository.Active()
        let! due = repository.Due()

        let text =
            match box interaction.User with
            | :? IGuildUser as admin ->
                Check.report
                    (context.Client.GetGuild context.GuildId)
                    settings
                    (settings.IsSome && hidden.Value = settings)
                    admin
                    active.Length
                    due.Length
            | _ -> "Use this in the server."

        do! interaction.FollowupAsync(MessageEmbed.shorten 2000 text, ephemeral = true) :> Task
    }

let private durationOption =
    let builder =
        (option "duration" "How long to lock yourself out" ApplicationCommandOptionType.Integer).WithRequired(true)

    for label, hours in durations do
        builder.AddChoice(label, hours) |> ignore

    builder

let commands (context: Context) (hidden: LockoutSettings option ref) (repository: LockoutRepository) : Command list =
    [
        {
            Name = "touch-grass"
            Description = "Lock yourself out of the server for a while, so you can study"
            Options = [ durationOption ]
            Permission = None
            Handle = ask context hidden repository
        }
        {
            Name = "touch-grass-check"
            Description = "Check that /touch-grass is set up correctly"
            Options = []
            Permission = Some GuildPermission.Administrator
            Handle = inspect context hidden repository
        }
    ]
