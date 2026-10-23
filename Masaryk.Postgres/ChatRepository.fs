namespace Masaryk.Postgres

open System.Threading.Tasks
open Npgsql

type ChatRepository(dataSource: NpgsqlDataSource) =
    member _.RecordReply(messageId: uint64, channelId: uint64, triggerMessageId: uint64) : Task<unit> =
        Sql.execute
            (dataSource.CreateCommand
                "INSERT INTO llm_reply (message_id, channel_id, trigger_message_id) VALUES ($1, $2, $3)")
            [
                box (decimal messageId)
                box (decimal channelId)
                box (decimal triggerMessageId)
            ]

    member _.FindReplies(messageIds: uint64 list) : Task<Set<uint64>> =
        task {
            let! found =
                Sql.query
                    (dataSource.CreateCommand "SELECT message_id FROM llm_reply WHERE message_id = ANY($1)")
                    [ Sql.ids messageIds ]
                    (fun r -> uint64 (r.GetDecimal 0))

            return Set.ofList found
        }
