/// Loads courses into the catalog, from a downloaded export with `import-courses` or straight
/// from IS with `fetch-courses`.
module Masaryk.Core.Services.Courses.Import

open System.IO
open System.Net.Http
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

type Fetched =
    {
        Term: string
        Courses: CatalogCourse list
        /// Faculty abbreviation and why its request failed.
        Failed: (string * string) list
    }

/// One request per faculty, so a failed one costs only its own courses.
let private fetchTerm (http: HttpClient) (credentials: Prezentator.Credentials) (term: string) : Task<Fetched> =
    task {
        let courses = ResizeArray<CatalogCourse>()
        let failed = ResizeArray<string * string>()

        for faculty in Faculties.all do
            try
                let! found = Prezentator.courses http credentials faculty term
                courses.AddRange found
                printfn $"{faculty.Abbreviation} {term}: {found.Length} courses."
            with
            | Prezentator.Refused _ as refused -> raise refused
            | error ->
                let reason = error.GetBaseException().Message
                eprintfn $"{faculty.Abbreviation} {term}: {reason}"
                failed.Add(faculty.Abbreviation, reason)

        return
            {
                Term = term
                Courses = List.ofSeq courses
                Failed = List.ofSeq failed
            }
    }

/// Imports all terms at once, so a course keeps its newest name.
let fetchAll
    (repository: CourseRepository)
    (http: HttpClient)
    (credentials: Prezentator.Credentials)
    (terms: string list)
    : Task<Fetched list * int * int> =
    task {
        let fetched = ResizeArray<Fetched>()

        for term in terms do
            let! result = fetchTerm http credentials term
            fetched.Add result

        // Newest term first, because Import keeps the first row of each course.
        let courses = fetched |> Seq.rev |> Seq.collect _.Courses |> List.ofSeq
        let! added, renamed = repository.Import courses
        return List.ofSeq fetched, added, renamed
    }

let fetch (database: NpgsqlDataSource) (terms: string list) : Task<unit> =
    task {
        match Prezentator.credentials () with
        | None -> failwith "Set IS_UCO and IS_PASSWORD to the neosobní účet to fetch courses from IS."
        | Some credentials ->
            use http = Prezentator.client ()
            let! _, added, renamed = fetchAll (CourseRepository database) http credentials terms
            printfn $"{added} new, {renamed} renamed."
    }
