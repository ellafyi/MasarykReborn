CREATE TABLE llm_reply
(
    message_id         discord_snowflake PRIMARY KEY,
    channel_id         discord_snowflake NOT NULL,
    trigger_message_id discord_snowflake NOT NULL,
    created_at         timestamptz       NOT NULL DEFAULT now()
);
