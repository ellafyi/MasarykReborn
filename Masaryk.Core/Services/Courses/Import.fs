/// Loads courses into the catalog, from a downloaded export with `import-courses` or straight
/// from IS with `fetch-courses`.
module Masaryk.Core.Services.Courses.Import

open System.IO
open System.Threading.Tasks
open Npgsql
open Masaryk.IS
open Masaryk.Postgres

let run (database: NpgsqlDataSource) (path: string) : Task<unit> =
    task {
        let! html = File.ReadAllTextAsync path
        let courses = Export.parse html
        printfn $"Read {courses.Length} courses from {path}."
        let! added, renamed = CourseRepository(database).Import courses
        printfn $"{added} new, {renamed} renamed."
    }

/// One request per faculty and term, so a failed one costs only its own courses.
/// Later terms rename courses, so list them oldest first.
let fetch (database: NpgsqlDataSource) (terms: string list) : Task<unit> =
    task {
        match Prezentator.credentials () with
        | None -> failwith "Set IS_UCO and IS_PASSWORD to the neosobní účet to fetch courses from IS."
        | Some credentials ->
            use http = Prezentator.client ()
            let repository = CourseRepository(database)

            for term in terms do
                for faculty in Faculties.all do
                    try
                        let! courses = Prezentator.courses http credentials faculty term
                        let! added, renamed = repository.Import courses

                        printfn
                            $"{faculty.Abbreviation} {term}: {courses.Length} courses, {added} new, {renamed} renamed."
                    with
                    | Prezentator.Refused _ as refused -> raise refused
                    | error -> eprintfn $"{faculty.Abbreviation} {term}: {error.GetBaseException().Message}"
    }
