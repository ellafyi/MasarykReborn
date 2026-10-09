namespace Masaryk.Postgres

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql
open Masaryk.Postgres.Starboard

module private Sql =
    /// Box the value in Some, or use SQL NULL for None.
    let optional (value: 'T option) : obj =
        value |> Option.map box |> Option.defaultValue (box DBNull.Value)

    /// Read the column with read, or return None for SQL NULL.
    let readOptional (reader: DbDataReader) (index: int) (read: int -> 'T) : 'T option =
        if reader.IsDBNull index then None else Some(read index)

    /// Bind values to $1, $2, etc. in list order, then return the same command.
    /// This only adds parameters; it doesn't call Npgsql's Prepare method.
    let prepare (command: NpgsqlCommand) (values: obj seq) : NpgsqlCommand =
        for value in values do
            command.Parameters.Add(NpgsqlParameter(Value = value)) |> ignore

        command

    /// Run the query and return its first value, such as an ID from INSERT ... RETURNING.
    /// Return null if no rows match. Dispose the command when done.
    let scalar (command: NpgsqlCommand) (values: obj seq) : Task<obj> =
        task {
            use command = prepare command values
            return! command.ExecuteScalarAsync()
        }

    /// Run a SQL statement and ignore how many rows it changes.
    /// Dispose the command when done.
    let execute (command: NpgsqlCommand) (values: obj seq) : Task<unit> =
        task {
            use command = prepare command values
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    /// Call read for each result row and return the results as a list.
    /// Return [] if no rows match. Dispose the reader and command when done.
    let query (command: NpgsqlCommand) (values: obj seq) (read: DbDataReader -> 'T) : Task<'T list> =
        task {
            use command = prepare command values
            use! reader = command.ExecuteReaderAsync()
            let results = ResizeArray()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync()
                hasRow <- next

                if next then
                    results.Add(read reader)

            return List.ofSeq results
        }

    /// Call read for the first result row, or return None if no rows match.
    /// Dispose the reader and command when done.
    let queryOne (command: NpgsqlCommand) (values: obj seq) (read: DbDataReader -> 'T) : Task<'T option> =
        task {
            use command = prepare command values
            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()
            return if found then Some(read reader) else None
        }

/// Load configuration, update board records, and save message captures.
type StarboardRepository(dataSource: NpgsqlDataSource) =
    /// Create a command using the connection pool. The SQL helpers dispose it.
    let command (sql: string) : NpgsqlCommand = dataSource.CreateCommand(sql)

    /// Load the guild's configuration as JSON text, creating an empty list if needed.
    /// Core parses and validates the JSON.
    member _.GetConfiguration(guildId: uint64) : Task<string> =
        task {
            let values = [ box (decimal guildId) ]

            do!
                Sql.execute
                    (command "INSERT INTO starboard_configuration (guild_id) VALUES ($1) ON CONFLICT DO NOTHING")
                    values

            let! json =
                Sql.scalar
                    (command "SELECT configuration::text FROM starboard_configuration WHERE guild_id = $1")
                    values

            return unbox<string> json
        }

    /// Save the validated board list in one transaction and return each board's name and ID.
    /// Keep IDs for existing names. Mark missing boards inactive and keep their captures.
    /// Leave the configuration JSON unchanged.
    member _.SynchronizeBoards(guildId: uint64, boards: BoardConfiguration list) : Task<Map<string, int64>> =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()

            /// Create a command using this transaction.
            let command (sql: string) : NpgsqlCommand =
                new NpgsqlCommand(sql, connection, transaction)

            let sql =
                """
                INSERT INTO starboard (guild_id, name, destination_channel_id, reaction_threshold, is_active)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (guild_id, name) DO UPDATE SET
                    destination_channel_id = EXCLUDED.destination_channel_id,
                    reaction_threshold = EXCLUDED.reaction_threshold,
                    is_active = EXCLUDED.is_active
                RETURNING id
                """

            let ids = ResizeArray()

            for board in boards do
                let! id =
                    Sql.scalar
                        (command sql)
                        [
                            box (decimal guildId)
                            box board.Name
                            box (decimal board.Target)
                            box board.Threshold
                            box board.IsActive
                        ]

                ids.Add(board.Name, unbox<int64> id)

            let names = boards |> List.map _.Name |> List.toArray

            do!
                Sql.execute
                    (command "UPDATE starboard SET is_active = false WHERE guild_id = $1 AND NOT (name = ANY($2))")
                    [ box (decimal guildId); box names ]

            do! transaction.CommitAsync()
            return ids |> Map.ofSeq
        }

    /// Check whether we already captured this message without loading its snapshot.
    member _.HasEntry(sourceMessageId: uint64) : Task<bool> =
        task {
            let! exists =
                Sql.scalar
                    (command "SELECT EXISTS (SELECT 1 FROM starboard_entry WHERE source_message_id = $1)")
                    [ box (decimal sourceMessageId) ]

            return unbox<bool> exists
        }

    /// Save the entry, source channel, reactions, attachments, and embeds in one transaction.
    /// Return Some entry ID, or None if a capture already exists for this message.
    /// Keep existing captures unchanged. Roll back all inserts if a write fails.
    member _.SaveSnapshot(snapshot: MessageSnapshot) : Task<int64 option> =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! transaction = connection.BeginTransactionAsync()

            /// Create a command using this transaction.
            let command (sql: string) : NpgsqlCommand =
                new NpgsqlCommand(sql, connection, transaction)

            /// Run a write in this transaction, binding values to $1, $2, etc.
            let execute (sql: string) (values: obj seq) : Task<unit> = Sql.execute (command sql) values
            // Store the thread's own ID when the message comes from a thread.
            do!
                execute
                    "INSERT INTO source_channel (discord_channel_id) VALUES ($1) ON CONFLICT DO NOTHING"
                    [ box (decimal snapshot.SourceChannelId) ]

            let insertSql =
                """
            INSERT INTO starboard_entry
                (starboard_id, source_channel_id, source_message_id, source_created_at,
                 author_user_id, author_display_name, content_snapshot, source_jump_url,
                 qualifying_emoji_key, qualifying_emoji_name, qualifying_count)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT (source_message_id) DO NOTHING RETURNING id
            """

            let! result =
                Sql.scalar
                    (command insertSql)
                    [
                        box snapshot.BoardId
                        box (decimal snapshot.SourceChannelId)
                        box (decimal snapshot.SourceMessageId)
                        box snapshot.SourceCreatedAt
                        Sql.optional (Option.map decimal snapshot.AuthorUserId)
                        box snapshot.AuthorDisplayName
                        box snapshot.Content
                        box snapshot.SourceJumpUrl
                        box snapshot.QualifyingEmojiKey
                        box snapshot.QualifyingEmojiName
                        box snapshot.QualifyingCount
                    ]

            if isNull result then
                return None
            else
                let entryId = unbox<int64> result

                for reaction in snapshot.Reactions do
                    do!
                        execute
                            "INSERT INTO entry_reaction (entry_id, emoji_key, emoji_name, count_snapshot) VALUES ($1, $2, $3, $4)"
                            [
                                box entryId
                                box reaction.EmojiKey
                                box reaction.EmojiName
                                box reaction.RawCount
                            ]

                for position, attachment in List.indexed snapshot.Attachments do
                    do!
                        execute
                            """
                    INSERT INTO entry_attachment (entry_id, position, discord_attachment_id, filename, url, media_type, size_bytes)
                    VALUES ($1, $2, $3, $4, $5, $6, $7)
                    """
                            [
                                box entryId
                                box position
                                box (decimal attachment.Id)
                                box attachment.Filename
                                box attachment.Url
                                Sql.optional attachment.MediaType
                                Sql.optional attachment.SizeBytes
                            ]

                for position, metadata in List.indexed snapshot.Embeds do
                    do!
                        execute
                            "INSERT INTO entry_embed (entry_id, position, metadata) VALUES ($1, $2, $3::jsonb)"
                            [ box entryId; box position; box metadata ]

                do! transaction.CommitAsync()
                return Some entryId
        }

    /// Find a capture by its source message ID and load its reactions, attachments, and embeds.
    /// Return None if no capture exists. Keep attachments and embeds in their original order.
    member _.FindEntry(sourceMessageId: uint64) : Task<Entry option> =
        task {
            use! connection = dataSource.OpenConnectionAsync()

            /// Use the same connection for all parts of the capture.
            let command (sql: string) : NpgsqlCommand = new NpgsqlCommand(sql, connection)

            let sql =
                """
            SELECT id, captured_at, starboard_id, source_channel_id, source_message_id,
                   source_created_at, author_user_id, author_display_name, content_snapshot,
                   source_jump_url, qualifying_emoji_key, qualifying_emoji_name, qualifying_count
            FROM starboard_entry WHERE source_message_id = $1
            """

            let! entry =
                Sql.queryOne (command sql) [ box (decimal sourceMessageId) ] (fun r ->
                    {
                        Id = r.GetInt64 0
                        CapturedAt = r.GetDateTime 1
                        Snapshot =
                            {
                                BoardId = r.GetInt64 2
                                SourceChannelId = uint64 (r.GetDecimal 3)
                                SourceMessageId = uint64 (r.GetDecimal 4)
                                SourceCreatedAt = r.GetDateTime 5
                                AuthorUserId = Sql.readOptional r 6 (fun i -> uint64 (r.GetDecimal i))
                                AuthorDisplayName = r.GetString 7
                                Content = r.GetString 8
                                SourceJumpUrl = r.GetString 9
                                QualifyingEmojiKey = r.GetString 10
                                QualifyingEmojiName = r.GetString 11
                                QualifyingCount = r.GetInt32 12
                                Reactions = []
                                Attachments = []
                                Embeds = []
                            }
                    })

            match entry with
            | None -> return None
            | Some entry ->
                let parameters = [ box entry.Id ]

                let! reactions =
                    Sql.query
                        (command
                            "SELECT emoji_key, emoji_name, count_snapshot FROM entry_reaction WHERE entry_id = $1 ORDER BY emoji_key")
                        parameters
                        (fun r ->
                            {
                                EmojiKey = r.GetString 0
                                EmojiName = r.GetString 1
                                RawCount = r.GetInt32 2
                            })

                let attachmentsSql =
                    """
                SELECT discord_attachment_id, filename, url, media_type, size_bytes
                FROM entry_attachment WHERE entry_id = $1 ORDER BY position
                """

                let! attachments =
                    Sql.query (command attachmentsSql) parameters (fun r ->
                        {
                            Id = uint64 (r.GetDecimal 0)
                            Filename = r.GetString 1
                            Url = r.GetString 2
                            MediaType = Sql.readOptional r 3 r.GetString
                            SizeBytes = Sql.readOptional r 4 r.GetInt64
                        })

                let! embeds =
                    Sql.query
                        (command "SELECT metadata::text FROM entry_embed WHERE entry_id = $1 ORDER BY position")
                        parameters
                        (fun r -> r.GetString 0)

                let snapshot =
                    { entry.Snapshot with
                        Reactions = reactions
                        Attachments = attachments
                        Embeds = embeds
                    }

                return Some { entry with Snapshot = snapshot }
        }
