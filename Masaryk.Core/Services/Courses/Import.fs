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
        let! changes = CourseRepository(database).Import(Path.GetFileName path, courses)

        printfn
            $"Recorded {changes.Imports} faculty terms: {changes.Added} new, {changes.Changed} changed, {changes.Removed} no longer listed."
    }
