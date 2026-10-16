/// Small helpers for parameterized Npgsql commands, shared by the repositories.
module internal Masaryk.Postgres.Sql

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql

/// Box the value in Some, or use SQL NULL for None.
let optional (value: 'T option) : obj =
    value |> Option.map box |> Option.defaultValue (box DBNull.Value)

/// Read the column with read, or return None for SQL NULL.
let readOptional (reader: DbDataReader) (index: int) (read: int -> 'T) : 'T option =
    if reader.IsDBNull index then None else Some(read index)

/// Bind values to $1, $2, etc. in list order, then return the same command.
/// This only adds parameters; it doesn't call Npgsql's Prepare method.
let prepare (command: NpgsqlCommand) (values: obj seq) : NpgsqlCommand =
    for value in values do
        command.Parameters.Add(NpgsqlParameter(Value = value)) |> ignore

    command

/// Run the query and return its first value, such as an ID from INSERT ... RETURNING.
/// Return null if no rows match. Dispose the command when done.
let scalar (command: NpgsqlCommand) (values: obj seq) : Task<obj> =
    task {
        use command = prepare command values
        return! command.ExecuteScalarAsync()
    }

/// Run a SQL statement and ignore how many rows it changes.
/// Dispose the command when done.
let execute (command: NpgsqlCommand) (values: obj seq) : Task<unit> =
    task {
        use command = prepare command values
        let! _ = command.ExecuteNonQueryAsync()
        return ()
    }

/// Call read for each result row and return the results as a list.
/// Return [] if no rows match. Dispose the reader and command when done.
let query (command: NpgsqlCommand) (values: obj seq) (read: DbDataReader -> 'T) : Task<'T list> =
    task {
        use command = prepare command values
        use! reader = command.ExecuteReaderAsync()
        let results = ResizeArray()
        let mutable hasRow = true

        while hasRow do
            let! next = reader.ReadAsync()
            hasRow <- next

            if next then
                results.Add(read reader)

        return List.ofSeq results
    }

/// Call read for the first result row, or return None if no rows match.
/// Dispose the reader and command when done.
let queryOne (command: NpgsqlCommand) (values: obj seq) (read: DbDataReader -> 'T) : Task<'T option> =
    task {
        use command = prepare command values
        use! reader = command.ExecuteReaderAsync()
        let! found = reader.ReadAsync()
        return if found then Some(read reader) else None
    }
