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

/// Whether a source message is already on a starboard.
type StarboardRecord =
    /// Posted by this bot; the count it shows can be updated.
    | Posted of StarboardPost
    /// Posted by the old bot, which this bot can't edit.
    | Legacy

type StarboardRepository(dataSource: NpgsqlDataSource) =
    let command (sql: string) = dataSource.CreateCommand sql

    member _.Find(sourceMessageId: uint64) : Task<StarboardRecord option> =
        Sql.queryOne
            (command
                "SELECT target_channel_id, post_message_id, emoji, count FROM starboard_post WHERE source_message_id = $1")
            [ box (decimal sourceMessageId) ]
            (fun r ->
                if r.IsDBNull 2 then
                    Legacy
                else
                    Posted
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

    /// Record the old bot's posts as (source message, the new board's channel, old post)
    /// and return how many were new.
    member _.SaveLegacy(posts: (uint64 * uint64 * uint64) list) : Task<int> =
        let column pick =
            posts |> List.map (pick >> decimal) |> List.toArray |> box

        Sql.affected
            (command
                """
                INSERT INTO starboard_post (source_message_id, target_channel_id, post_message_id)
                SELECT * FROM unnest($1::numeric[], $2::numeric[], $3::numeric[])
                ON CONFLICT (source_message_id) DO NOTHING
                """)
            [
                column (fun (source, _, _) -> source)
                column (fun (_, channel, _) -> channel)
                column (fun (_, _, post) -> post)
            ]
