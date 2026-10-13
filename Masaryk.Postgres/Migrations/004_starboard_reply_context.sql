ALTER TABLE starboard_entry
    ADD COLUMN reply_context_truncated boolean NOT NULL DEFAULT false;

CREATE TABLE entry_reply
(
    entry_id            bigint            NOT NULL REFERENCES starboard_entry (id) ON DELETE CASCADE,
    position            integer           NOT NULL CHECK (position >= 0),
    source_channel_id   discord_snowflake NOT NULL,
    source_message_id   discord_snowflake NOT NULL,
    author_display_name text,
    content_snapshot    text,
    source_jump_url     text              NOT NULL,
    PRIMARY KEY (entry_id, position),
    CHECK ((author_display_name IS NULL) = (content_snapshot IS NULL))
);
