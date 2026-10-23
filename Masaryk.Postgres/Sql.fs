/// Small helpers for parameterized Npgsql commands, shared by the repositories.
/// The helpers that run a command dispose it, and its reader, when they finish.
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

/// Discord IDs as a `numeric[]` parameter.
let ids (values: uint64 seq) : obj =
    values |> Seq.map decimal |> Seq.toArray |> box

/// The start of the period in $1 ("all", "year", "month" or "week") as a local Prague
/// timestamp; "all" is -infinity.
let periodStart =
    "CASE WHEN $1 = 'all' THEN '-infinity'::timestamp ELSE date_trunc($1, now() AT TIME ZONE 'Europe/Prague') END"

/// Bind values to $1, $2, etc. in list order, then return the same command.
/// This only adds parameters; it doesn't call Npgsql's Prepare method.
let prepare (command: NpgsqlCommand) (values: obj seq) : NpgsqlCommand =
    for value in values do
        command.Parameters.Add(NpgsqlParameter(Value = value)) |> ignore

    command

/// A command that runs inside the transaction.
let command (transaction: NpgsqlTransaction) (sql: string) =
    new NpgsqlCommand(sql, transaction.Connection, transaction)

/// Run the query and return its first value, such as an ID from INSERT ... RETURNING.
/// Return null if no rows match.
let scalar (command: NpgsqlCommand) (values: obj seq) : Task<obj> =
    task {
        use command = prepare command values
        return! command.ExecuteScalarAsync()
    }

/// Run a SQL statement and ignore how many rows it changes.
let execute (command: NpgsqlCommand) (values: obj seq) : Task<unit> =
    task {
        use command = prepare command values
        let! _ = command.ExecuteNonQueryAsync()
        return ()
    }

/// Run a SQL statement and return how many rows it changed.
let affected (command: NpgsqlCommand) (values: obj seq) : Task<int> =
    task {
        use command = prepare command values
        return! command.ExecuteNonQueryAsync()
    }

/// Call read for each result row and return the results as a list.
/// Return [] if no rows match.
let query (command: NpgsqlCommand) (values: obj seq) (read: DbDataReader -> 'T) : Task<'T list> =
    task {
        use command = prepare command values
        use! reader = command.ExecuteReaderAsync()
        let results = ResizeArray()

        while! reader.ReadAsync() do
            results.Add(read reader)

        return List.ofSeq results
    }

/// Call read for the first result row, or return None if no rows match.
let queryOne (command: NpgsqlCommand) (values: obj seq) (read: DbDataReader -> 'T) : Task<'T option> =
    task {
        use command = prepare command values
        use! reader = command.ExecuteReaderAsync()
        let! found = reader.ReadAsync()
        return if found then Some(read reader) else None
    }

/// When live counting started for a feature, recorded in its single-row state table on the first run.
let startCounting (dataSource: NpgsqlDataSource) (stateTable: string) : Task<DateTimeOffset> =
    task {
        let! started =
            scalar
                (dataSource.CreateCommand
                    $"""
                    INSERT INTO {stateTable} (id, counting_since) VALUES (1, now())
                    ON CONFLICT (id) DO UPDATE SET id = 1
                    RETURNING counting_since
                    """)
                []

        return DateTimeOffset(unbox<DateTime> started)
    }
