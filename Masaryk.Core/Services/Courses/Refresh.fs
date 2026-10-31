/// Keeps the course catalog current, once a day at 04:00.
module Masaryk.Core.Services.Courses.Refresh

open System
open System.Threading.Tasks
open Discord
open Masaryk.Core
open Masaryk.Core.Services
open Masaryk.Core.Services.Shared
open Masaryk.IS
open Masaryk.Postgres

let private failures (fetched: Import.Fetched list) =
    [
        for result in fetched do
            match result.Failed with
            | [] -> ()
            | failed when failed.Length = Faculties.all.Length ->
                // Every faculty failing usually means IS doesn't know the term yet.
                $"{result.Term}: every request failed ({snd failed.Head})"
            | failed ->
                let faculties = failed |> List.map fst |> String.concat ", "
                $"{result.Term}: failed for {faculties} ({snd failed.Head})"
    ]

let private refresh (context: Context) (http: Net.Http.HttpClient) (credentials: Prezentator.Credentials) =
    task {
        let terms = Terms.ofAcademicYear (DateOnly.FromDateTime DateTime.Now)
        let repository = CourseRepository(context.Database)
        let! fetched, added, renamed = Import.fetchAll repository http credentials terms
        let problems = failures fetched
        let count = fetched |> List.sumBy _.Courses.Length
        let terms = String.concat ", " terms

        let text =
            String.concat
                "\n"
                ($"Course catalog: {count} courses from {terms}, {added} new, {renamed} renamed."
                 :: problems)

        printfn $"{text}"

        // Daily runs that change nothing would only clutter the log channel.
        if added > 0 || renamed > 0 || not problems.IsEmpty then
            match Config.logChannel context with
            | Some channel -> do! channel.SendMessageAsync(text, allowedMentions = AllowedMentions.None) :> Task
            | None -> ()
    }

/// Runs forever. Off without the IS account.
let run (context: Context) (jobs: JobRepository) : Task =
    task {
        match Prezentator.credentials () with
        | None -> printfn "Course catalog refresh is off: set IS_UCO and IS_PASSWORD to turn it on."
        | Some credentials ->
            use http = Prezentator.client ()

            do!
                Schedule.daily jobs "Course catalog refresh" (TimeOnly(4, 0)) (fun () ->
                    refresh context http credentials)
    }
