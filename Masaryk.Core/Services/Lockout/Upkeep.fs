/// Every 30 seconds: give back roles when time is up, lock out rejoined members again, hide the
/// channels from the lockout role, and post the notice.
module Masaryk.Core.Services.Lockout.Upkeep

open System
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Configuration
open Masaryk.Core
open Masaryk.Postgres

let private interval = TimeSpan.FromSeconds 30.0

/// Log a problem once, not on every pass of the loop.
let private report (last: string ref) (message: string) =
    if last.Value <> message then
        eprintfn $"{message}"
        last.Value <- message

/// /touch-grass works only while every channel is hidden from the lockout role.
let private hide
    (context: Context)
    (hidden: LockoutSettings option ref)
    (last: string ref)
    (guild: SocketGuild)
    : Task =
    task {
        match context.Configuration.Current.Lockout with
        | None -> hidden.Value <- None
        | Some settings when isNull (guild.GetRole settings.RoleId) ->
            hidden.Value <- None
            report last $"Touch grass: there's no role {settings.RoleId}, so /touch-grass is off."
        | Some settings ->
            let! outcome = Hiding.reconcile guild settings
            let failures = outcome.Failures

            if failures.IsEmpty then
                // Discord's cache lags behind changes, so only a pass that changes nothing proves the setup.
                hidden.Value <- if outcome.Changed = 0 then Some settings else None
                last.Value <- ""
            else
                hidden.Value <- None

                let sample =
                    failures
                    |> List.truncate 3
                    |> List.map (fun (id, reason) -> $"{id}: {reason}")
                    |> String.concat "; "

                report
                    last
                    $"Touch grass: can't hide {failures.Length} channels from the lockout role, so /touch-grass is off until I can ({sample})."
    }

/// Give back the roles and own overwrites of everyone whose time is up. Members who left just lose
/// their row. An error keeps the row, and what's left is retried on the next pass.
let private restoreDue (context: Context) (repository: LockoutRepository) (guild: SocketGuild) : Task =
    task {
        let! due = repository.Due()

        for locked in due do
            try
                let! user = context.Client.Rest.GetGuildUserAsync(context.GuildId, locked.UserId)

                if not (isNull user) then
                    do! Members.restore guild user locked
                    let! saved = repository.Overwrites locked.UserId

                    for overwrite in saved do
                        do! Members.giveBack guild user overwrite
                        do! repository.RemoveOverwrite(locked.UserId, overwrite.ChannelId)

                do! repository.Remove locked.UserId
            with error ->
                eprintfn $"Touch grass: couldn't restore {locked.UserId}, will retry: {error.Message}"
    }

/// Locks out again anyone who rejoined, and takes away own overwrites that a failed removal left
/// behind or an admin added since.
let private enforce (context: Context) (repository: LockoutRepository) (guild: SocketGuild) : Task =
    task {
        let! active = repository.Active()

        for locked in active do
            try
                let! user = context.Client.Rest.GetGuildUserAsync(context.GuildId, locked.UserId)

                if not (isNull user) then
                    if Members.rejoined user locked then
                        do! Members.lock guild locked.LockRole user
                        printfn $"Touch grass: {locked.UserId} rejoined during a lockout and is locked out again."

                    do! Members.takeOverwrites repository guild user
            with error ->
                eprintfn $"Touch grass: couldn't re-check {locked.UserId}: {error.Message}"
    }

/// Post the notice once per settings, after every channel is hidden.
let private announce
    (context: Context)
    (hidden: LockoutSettings option ref)
    (announced: LockoutSettings option ref)
    (last: string ref)
    (guild: SocketGuild)
    : Task =
    task {
        match hidden.Value with
        | Some settings when announced.Value <> Some settings ->
            match guild.GetTextChannel settings.ChannelId with
            | null -> ()
            | channel ->
                try
                    let! posted = Notice.ensure context.Client channel

                    if posted then
                        printfn $"Touch grass: posted the notice in {channel.Id}."

                    announced.Value <- Some settings
                    last.Value <- ""
                with error ->
                    report last $"Touch grass: couldn't post the notice in {channel.Id}: {error.Message}"
        | _ -> ()
    }

/// Runs forever. `hidden` holds the settings once every channel is hidden from their role, else None.
let run (context: Context) (repository: LockoutRepository) (hidden: LockoutSettings option ref) : Task =
    task {
        let last = ref ""
        let lastNotice = ref ""
        let announced: LockoutSettings option ref = ref None

        while true do
            try
                let guild = context.Client.GetGuild context.GuildId

                if context.Client.ConnectionState = ConnectionState.Connected && not (isNull guild) then
                    do! restoreDue context repository guild
                    do! enforce context repository guild
                    do! hide context hidden last guild
                    do! announce context hidden announced lastNotice guild
            with error ->
                eprintfn $"Touch grass: {error.Message}"

            do! Task.Delay interval
    }
