namespace Masaryk.Postgres

open System
open System.Threading.Tasks
open Npgsql

type JobRepository(dataSource: NpgsqlDataSource) =
    member _.LastRun(name: string) : Task<DateTimeOffset option> =
        Sql.queryOne
            (dataSource.CreateCommand "SELECT last_run FROM scheduled_job WHERE name = $1")
            [ box name ]
            _.GetFieldValue<DateTimeOffset>(0)

    member _.MarkRun(name: string, at: DateTimeOffset) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                """
                INSERT INTO scheduled_job (name, last_run) VALUES ($1, $2)
                ON CONFLICT (name) DO UPDATE SET last_run = EXCLUDED.last_run
                """)
            [ box name; box at ]
