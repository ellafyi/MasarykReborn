namespace Masaryk.Postgres

open System.Threading.Tasks
open Npgsql

/// A message already on the starboard. Emoji is how Discord writes it: ⭐ or <:name:id>.
type StarboardPost =
    {
        TargetChannelId: uint64
        PostMessageId: uint64
        Emoji: string
        Count: int
    }

type StarboardRepository(dataSource: NpgsqlDataSource) =
    let command (sql: string) = dataSource.CreateCommand sql

    /// The guild's board list as JSON, creating an empty list if needed.
    member _.GetConfiguration(guildId: uint64) : Task<string> =
        task {
            let! json =
                Sql.scalar
                    (command
                        """
                        INSERT INTO starboard_configuration (guild_id) VALUES ($1)
                        ON CONFLICT (guild_id) DO UPDATE SET guild_id = EXCLUDED.guild_id
                        RETURNING configuration::text
                        """)
                    [ box (decimal guildId) ]

            return unbox<string> json
        }

    member _.Find(sourceMessageId: uint64) : Task<StarboardPost option> =
        Sql.queryOne
            (command
                "SELECT target_channel_id, post_message_id, emoji, count FROM starboard_post WHERE source_message_id = $1")
            [ box (decimal sourceMessageId) ]
            (fun r ->
                {
                    TargetChannelId = uint64 (r.GetDecimal 0)
                    PostMessageId = uint64 (r.GetDecimal 1)
                    Emoji = r.GetString 2
                    Count = r.GetInt32 3
                })

    member _.Save(sourceMessageId: uint64, post: StarboardPost) : Task<unit> =
        Sql.execute
            (command
                "INSERT INTO starboard_post (source_message_id, target_channel_id, post_message_id, emoji, count) VALUES ($1, $2, $3, $4, $5)")
            [
                box (decimal sourceMessageId)
                box (decimal post.TargetChannelId)
                box (decimal post.PostMessageId)
                box post.Emoji
                box post.Count
            ]

    member _.SetCount(sourceMessageId: uint64, count: int) : Task<unit> =
        Sql.execute
            (command "UPDATE starboard_post SET count = $2 WHERE source_message_id = $1")
            [ box (decimal sourceMessageId); box count ]
