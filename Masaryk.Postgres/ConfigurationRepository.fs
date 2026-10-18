namespace Masaryk.Postgres

open System
open System.Threading.Tasks
open Npgsql

type ConfigurationSave =
    | Saved of version: int64
    /// Someone saved a newer revision since the one this edit started from.
    | Stale of current: int64

/// Revisions of each guild's configuration document, stored as given.
type ConfigurationRepository(dataSource: NpgsqlDataSource) =
    /// The newest revision's number and document; revision 0 means none was saved yet.
    member _.Latest(guildId: uint64) : Task<int64 * string option> =
        task {
            let! found =
                Sql.queryOne
                    (dataSource.CreateCommand
                        "SELECT id, document::text FROM configuration_revision WHERE guild_id = $1 ORDER BY id DESC LIMIT 1")
                    [ box (decimal guildId) ]
                    (fun r -> r.GetInt64 0, r.GetString 1)

            return
                match found with
                | Some(version, document) -> version, Some document
                | None -> 0L, None
        }

    /// Save a new revision if `basedOn` is still the newest one.
    member _.Save(guildId: uint64, basedOn: int64, document: string, savedBy: uint64) : Task<ConfigurationSave> =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()

            let command (sql: string) =
                new NpgsqlCommand(sql, connection, transaction)

            do! Sql.execute (command "LOCK TABLE configuration_revision IN EXCLUSIVE MODE") []

            let! current =
                Sql.scalar
                    (command "SELECT coalesce(max(id), 0) FROM configuration_revision WHERE guild_id = $1")
                    [ box (decimal guildId) ]

            let current = unbox<int64> current

            if current <> basedOn then
                return Stale current
            else
                let! version =
                    Sql.scalar
                        (command
                            "INSERT INTO configuration_revision (guild_id, document, saved_by) VALUES ($1, $2::jsonb, $3) RETURNING id")
                        [ box (decimal guildId); box document; box (decimal savedBy) ]

                do! transaction.CommitAsync()
                return Saved(unbox<int64> version)
        }

    /// Call `changed` whenever any revision is saved, and after every (re)connection, since
    /// notifications sent while disconnected are lost. Runs until the process ends.
    member _.Listen(changed: unit -> unit) : Task =
        task {
            while true do
                try
                    use! connection = dataSource.OpenConnectionAsync()
                    connection.add_Notification (NotificationEventHandler(fun _ _ -> changed ()))
                    do! Sql.execute (new NpgsqlCommand("LISTEN configuration", connection)) []
                    changed ()

                    while true do
                        do! connection.WaitAsync()
                with error ->
                    printfn $"Configuration: lost database notifications ({error.Message}); retrying in 10 s."
                    do! Task.Delay(TimeSpan.FromSeconds 10.0)
        }
