namespace Masaryk.Postgres

open System
open System.Threading.Tasks
open Npgsql

/// One-off import of the old bot's message history, restored into the legacy schema.
type LegacyImport(dataSource: NpgsqlDataSource) =
    member _.Prepare() : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                """
                CREATE TABLE IF NOT EXISTS legacy.bots (author_id numeric PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS legacy.checked (author_id numeric PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS legacy.parents (channel_id numeric PRIMARY KEY, parent_id numeric NOT NULL);
                CREATE TABLE IF NOT EXISTS legacy.gap_messages
                (
                    message_id numeric PRIMARY KEY,
                    channel_id numeric NOT NULL,
                    author_id  numeric NOT NULL,
                    posted_at  timestamptz NOT NULL
                );
                -- Only bots post application command replies.
                INSERT INTO legacy.bots
                SELECT DISTINCT author_id::numeric FROM legacy.backed_up_messages WHERE type IN (20, 23)
                ON CONFLICT DO NOTHING;
                """)
            []

    member _.Imported() : Task<bool> =
        task {
            let! imported =
                Sql.scalar (dataSource.CreateCommand "SELECT history_imported_at IS NOT NULL FROM leaderboard_state") []

            return unbox<bool> imported
        }

    /// Authors with enough messages to matter in the rankings, not yet checked.
    member _.BusyAuthors(minimum: int) : Task<uint64 list> =
        Sql.query
            (dataSource.CreateCommand
                """
                SELECT author_id::numeric FROM legacy.backed_up_messages
                WHERE type IN (0, 19)
                GROUP BY 1 HAVING count(*) >= $1
                EXCEPT SELECT author_id FROM legacy.bots
                EXCEPT SELECT author_id FROM legacy.checked
                """)
            [ box minimum ]
            (fun r -> uint64 (r.GetDecimal 0))

    member _.Channels() : Task<uint64 list> =
        Sql.query
            (dataSource.CreateCommand "SELECT DISTINCT channel_id::numeric FROM legacy.backed_up_messages")
            []
            (fun r -> uint64 (r.GetDecimal 0))

    /// The newest old message per channel, where the gap fill continues from.
    member _.LastMessages() : Task<Map<uint64, uint64>> =
        task {
            let! rows =
                Sql.query
                    (dataSource.CreateCommand
                        "SELECT channel_id::numeric, max(message_id::numeric) FROM legacy.backed_up_messages GROUP BY 1")
                    []
                    (fun r -> uint64 (r.GetDecimal 0), uint64 (r.GetDecimal 1))

            return Map.ofList rows
        }

    /// When the old bot's backup ends; channels it never saw continue from here.
    member _.DumpEnd() : Task<DateTime> =
        task {
            let! last =
                Sql.scalar
                    (dataSource.CreateCommand "SELECT max(posted_at) AT TIME ZONE 'UTC' FROM legacy.backed_up_messages")
                    []

            return unbox<DateTime> last
        }

    member _.SaveAuthor(authorId: uint64, isBot: bool) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                """
                INSERT INTO legacy.checked VALUES ($1);
                INSERT INTO legacy.bots SELECT $1 WHERE $2 ON CONFLICT DO NOTHING;
                """)
            [ box (decimal authorId); box isBot ]

    member _.SaveParents(threads: (uint64 * uint64) list) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                "INSERT INTO legacy.parents SELECT * FROM unnest($1::numeric[], $2::numeric[]) ON CONFLICT DO NOTHING")
            [ Sql.ids (List.map fst threads); Sql.ids (List.map snd threads) ]

    member _.SaveGap(messages: (uint64 * uint64 * uint64 * DateTime) list) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                """
                INSERT INTO legacy.gap_messages
                SELECT * FROM unnest($1::numeric[], $2::numeric[], $3::numeric[], $4::timestamptz[])
                ON CONFLICT DO NOTHING
                """)
            [
                Sql.ids (messages |> List.map (fun (id, _, _, _) -> id))
                Sql.ids (messages |> List.map (fun (_, channel, _, _) -> channel))
                Sql.ids (messages |> List.map (fun (_, _, author, _) -> author))
                box (messages |> List.map (fun (_, _, _, posted) -> posted) |> List.toArray)
            ]

    /// Add the old and gap messages to the counts, once. Returns the number of messages added.
    member _.Finish() : Task<int64> =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()

            let command = Sql.command transaction

            let! imported =
                Sql.scalar (command "SELECT history_imported_at IS NOT NULL FROM leaderboard_state FOR UPDATE") []

            if unbox<bool> imported then
                failwith "The history has already been imported."

            let! added =
                Sql.scalar
                    (command
                        """
                        WITH messages AS (
                            SELECT coalesce(p.parent_id, m.channel_id::numeric) AS channel_id,
                                   m.author_id::numeric AS author_id,
                                   m.posted_at AT TIME ZONE 'UTC' AS posted_at
                            FROM legacy.backed_up_messages m
                            LEFT JOIN legacy.parents p ON p.channel_id = m.channel_id::numeric
                            WHERE m.type IN (0, 19) AND m.deleted_at IS NULL
                              AND NOT EXISTS (SELECT 1 FROM legacy.bots b WHERE b.author_id = m.author_id::numeric)
                            UNION ALL
                            SELECT channel_id, author_id, posted_at FROM legacy.gap_messages
                        ), grouped AS (
                            SELECT channel_id, author_id, (posted_at AT TIME ZONE 'Europe/Prague')::date AS day,
                                   count(*) AS count
                            FROM messages
                            WHERE posted_at < (SELECT counting_since FROM leaderboard_state)
                            GROUP BY 1, 2, 3
                        ), inserted AS (
                            INSERT INTO message_count (channel_id, author_id, day, count)
                            SELECT * FROM grouped
                            ON CONFLICT (channel_id, author_id, day)
                                DO UPDATE SET count = message_count.count + EXCLUDED.count
                        )
                        SELECT coalesce(sum(count), 0)::bigint FROM grouped
                        """)
                    []

            do! Sql.execute (command "UPDATE leaderboard_state SET history_imported_at = now()") []
            do! transaction.CommitAsync()
            return unbox<int64> added
        }
