/// Jobs that run once a day at a fixed local time.
module Masaryk.Core.Services.Shared.Schedule

open System
open System.Threading.Tasks
open Masaryk.Postgres

let private zone = TimeZoneInfo.FindSystemTimeZoneById "Europe/Prague"

/// The latest time the job was due, at or before `now`.
let private lastDue (at: TimeOnly) (now: DateTimeOffset) : DateTime =
    let local = TimeZoneInfo.ConvertTime(now, zone).DateTime
    let today = local.Date + at.ToTimeSpan()
    if local < today then today.AddDays -1.0 else today

let private utc (local: DateTime) =
    DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone))

/// Runs forever. Runs `work` daily at `at`, Prague time, and right away when the bot was down at
/// the last due time. Avoid 02:00 to 03:00, which daylight saving time skips or repeats.
let daily (jobs: JobRepository) (name: string) (at: TimeOnly) (work: unit -> Task) : Task =
    task {
        while true do
            let due = lastDue at DateTimeOffset.UtcNow
            let! last = jobs.LastRun name

            if last |> Option.forall (fun last -> last < utc due) then
                let started = DateTimeOffset.UtcNow

                try
                    do! work ()
                with error ->
                    eprintfn $"{name} failed: {error.GetBaseException().Message}"

                // A failed run waits for the next day too, instead of hitting IS or Discord again at once.
                do! jobs.MarkRun(name, started)

            let next = utc (due.AddDays 1.0) - DateTimeOffset.UtcNow
            do! Task.Delay(max next TimeSpan.Zero)
    }
