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
    static let since = $"({Sql.periodStart})::date"

    static let row (r: Data.Common.DbDataReader) =
        {
            Position = r.GetInt64 0
            Id = uint64 (r.GetDecimal 1)
            Messages = r.GetInt64 2
        }

    member _.StartCounting() : Task<DateTimeOffset> =
        Sql.startCounting dataSource "leaderboard_state"

    member _.Count(channelId: uint64, authorId: uint64, postedAt: DateTime) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                """
                INSERT INTO message_count (channel_id, author_id, day, count)
                VALUES ($1, $2, ($3 AT TIME ZONE 'Europe/Prague')::date, 1)
                ON CONFLICT (channel_id, author_id, day) DO UPDATE SET count = message_count.count + 1
                """)
            [ box (decimal channelId); box (decimal authorId); box postedAt ]

    /// The `top` users, plus the users up to `around` places either side of memberId.
    member _.Users
        (period: string, channelId: uint64 option, memberId: uint64, top: int, around: int)
        : Task<LeaderboardRow list> =
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
                WHERE position <= $4 OR abs(position - (SELECT position FROM member)) <= $5
                ORDER BY position
                """)
            [
                box period
                Sql.optional (Option.map decimal channelId)
                box (decimal memberId)
                box top
                box around
            ]
            row

    member _.Channels(period: string, top: int) : Task<LeaderboardRow list> =
        Sql.query
            (dataSource.CreateCommand
                $"""
                SELECT row_number() OVER (ORDER BY sum(count) DESC, channel_id), channel_id, sum(count)
                FROM message_count
                WHERE day >= {since}
                GROUP BY channel_id
                ORDER BY 1
                LIMIT $2
                """)
            [ box period; box top ]
            row
