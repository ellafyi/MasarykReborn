namespace Masaryk.Postgres

open System
open System.Threading.Tasks
open Npgsql

type LeaderboardRow =
    {
        Position: int64
        Id: uint64
        Messages: int64
    }

type LeaderboardRepository(dataSource: NpgsqlDataSource) =
    /// Days and period boundaries follow the server's local calendar.
    static let since =
        "CASE WHEN $1 = 'all' THEN '-infinity'::date ELSE date_trunc($1, now() AT TIME ZONE 'Europe/Prague')::date END"

    static let row (r: Data.Common.DbDataReader) =
        {
            Position = r.GetInt64 0
            Id = uint64 (r.GetDecimal 1)
            Messages = r.GetInt64 2
        }

    /// The time live counting started, set on the first run.
    member _.StartCounting() : Task<DateTime> =
        task {
            let! started =
                Sql.scalar
                    (dataSource.CreateCommand
                        """
                        INSERT INTO leaderboard_state (id, counting_since) VALUES (1, now())
                        ON CONFLICT (id) DO UPDATE SET id = 1
                        RETURNING counting_since
                        """)
                    []

            return unbox<DateTime> started
        }

    member _.Count(channelId: uint64, authorId: uint64, postedAt: DateTime) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                """
                INSERT INTO message_count (channel_id, author_id, day, count)
                VALUES ($1, $2, ($3 AT TIME ZONE 'Europe/Prague')::date, 1)
                ON CONFLICT (channel_id, author_id, day) DO UPDATE SET count = message_count.count + 1
                """)
            [ box (decimal channelId); box (decimal authorId); box postedAt ]

    /// The top 10 users, plus the two users either side of memberId.
    member _.Users(period: string, channelId: uint64 option, memberId: uint64) : Task<LeaderboardRow list> =
        Sql.query
            (dataSource.CreateCommand
                $"""
                WITH ranked AS (
                    SELECT row_number() OVER (ORDER BY sum(count) DESC, author_id) AS position,
                           author_id, sum(count) AS messages
                    FROM message_count
                    WHERE day >= {since} AND ($2::numeric IS NULL OR channel_id = $2)
                    GROUP BY author_id
                ), member AS (SELECT position FROM ranked WHERE author_id = $3)
                SELECT position, author_id, messages FROM ranked
                WHERE position <= 10 OR abs(position - (SELECT position FROM member)) <= 2
                ORDER BY position
                """)
            [
                box period
                Sql.optional (Option.map decimal channelId)
                box (decimal memberId)
            ]
            row

    member _.Channels(period: string) : Task<LeaderboardRow list> =
        Sql.query
            (dataSource.CreateCommand
                $"""
                SELECT row_number() OVER (ORDER BY sum(count) DESC, channel_id), channel_id, sum(count)
                FROM message_count
                WHERE day >= {since}
                GROUP BY channel_id
                ORDER BY 1
                LIMIT 10
                """)
            [ box period ]
            row
