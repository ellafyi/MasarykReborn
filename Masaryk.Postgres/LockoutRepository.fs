namespace Masaryk.Postgres

open System
open System.Threading.Tasks
open Npgsql

type LockedMember =
    {
        UserId: uint64
        /// Stored so the end removes it even if the configuration changed.
        LockRole: uint64
        /// The roles taken away.
        Roles: uint64 list
        LockedAt: DateTimeOffset
        Until: DateTimeOffset
    }

/// A member's own overwrite on one channel, saved while they are locked out.
type SavedOverwrite =
    {
        ChannelId: uint64
        Allow: uint64
        Deny: uint64
    }

/// Members locked out with /touch-grass. Times come from the database's clock.
type LockoutRepository(dataSource: NpgsqlDataSource) =
    static let columns = "user_id, lock_role_id, role_ids, locked_at, locked_until"

    static let row (r: Data.Common.DbDataReader) =
        {
            UserId = uint64 (r.GetDecimal 0)
            LockRole = uint64 (r.GetDecimal 1)
            Roles = r.GetFieldValue<decimal[]> 2 |> Array.map uint64 |> List.ofArray
            LockedAt = r.GetFieldValue<DateTimeOffset> 3
            Until = r.GetFieldValue<DateTimeOffset> 4
        }

    /// None if the member is already locked out.
    member _.Start(userId: uint64, lockRole: uint64, roles: uint64 list, length: TimeSpan) : Task<LockedMember option> =
        Sql.queryOne
            (dataSource.CreateCommand
                $"""
                INSERT INTO lockout (user_id, lock_role_id, role_ids, locked_until)
                VALUES ($1, $2, $3, now() + make_interval(secs => $4))
                ON CONFLICT (user_id) DO NOTHING
                RETURNING {columns}
                """)
            [
                box (decimal userId)
                box (decimal lockRole)
                box (roles |> List.map decimal |> List.toArray)
                box length.TotalSeconds
            ]
            row

    member _.Find(userId: uint64) : Task<LockedMember option> =
        Sql.queryOne
            (dataSource.CreateCommand $"SELECT {columns} FROM lockout WHERE user_id = $1")
            [ box (decimal userId) ]
            row

    member _.Due() : Task<LockedMember list> =
        Sql.query
            (dataSource.CreateCommand $"SELECT {columns} FROM lockout WHERE locked_until <= now() ORDER BY locked_until")
            []
            row

    member _.Active() : Task<LockedMember list> =
        Sql.query
            (dataSource.CreateCommand $"SELECT {columns} FROM lockout WHERE locked_until > now() ORDER BY locked_until")
            []
            row

    member _.Remove(userId: uint64) : Task<unit> =
        Sql.execute (dataSource.CreateCommand "DELETE FROM lockout WHERE user_id = $1") [ box (decimal userId) ]

    /// An overwrite already saved for a channel is kept, since it is the one from before the lockout.
    member _.SaveOverwrites(userId: uint64, overwrites: SavedOverwrite list) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                """
                INSERT INTO lockout_overwrite (user_id, channel_id, allow_value, deny_value)
                SELECT $1::numeric, * FROM unnest($2::numeric[], $3::numeric[], $4::numeric[])
                ON CONFLICT (user_id, channel_id) DO NOTHING
                """)
            [
                box (decimal userId)
                Sql.ids (overwrites |> List.map _.ChannelId)
                Sql.ids (overwrites |> List.map _.Allow)
                Sql.ids (overwrites |> List.map _.Deny)
            ]

    member _.Overwrites(userId: uint64) : Task<SavedOverwrite list> =
        Sql.query
            (dataSource.CreateCommand
                "SELECT channel_id, allow_value, deny_value FROM lockout_overwrite WHERE user_id = $1")
            [ box (decimal userId) ]
            (fun r ->
                {
                    ChannelId = uint64 (r.GetDecimal 0)
                    Allow = uint64 (r.GetDecimal 1)
                    Deny = uint64 (r.GetDecimal 2)
                })

    member _.RemoveOverwrite(userId: uint64, channelId: uint64) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand "DELETE FROM lockout_overwrite WHERE user_id = $1 AND channel_id = $2")
            [ box (decimal userId); box (decimal channelId) ]
