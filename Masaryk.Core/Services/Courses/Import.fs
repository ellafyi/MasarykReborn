/// `import-courses <file>`: load a downloaded IS export into the course catalog.
module Masaryk.Core.Services.Courses.Import

open System.IO
open System.Threading.Tasks
open Npgsql
open Masaryk.Postgres

let run (database: NpgsqlDataSource) (path: string) : Task<unit> =
    task {
        let! html = File.ReadAllTextAsync path
        let courses = Export.parse html
        printfn $"Read {courses.Length} courses from {path}."
        let! added, renamed = CourseRepository(database).Import courses
        printfn $"{added} new, {renamed} renamed."
    }
