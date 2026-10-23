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
    member _.Find(sourceMessageId: uint64) : Task<StarboardRecord option> =
        Sql.queryOne
            (dataSource.CreateCommand
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
            (dataSource.CreateCommand
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
            (dataSource.CreateCommand "UPDATE starboard_post SET count = $2 WHERE source_message_id = $1")
            [ box (decimal sourceMessageId); box count ]

    /// Record the old bot's posts on the board in `targetChannelId` as (source message, old post)
    /// pairs and return how many were new.
    member _.SaveLegacy(targetChannelId: uint64, posts: (uint64 * uint64) list) : Task<int> =
        Sql.affected
            (dataSource.CreateCommand
                """
                INSERT INTO starboard_post (source_message_id, target_channel_id, post_message_id)
                SELECT source, $1, post FROM unnest($2::numeric[], $3::numeric[]) AS t (source, post)
                ON CONFLICT (source_message_id) DO NOTHING
                """)
            [
                box (decimal targetChannelId)
                Sql.ids (List.map fst posts)
                Sql.ids (List.map snd posts)
            ]
