CREATE TABLE message_count
(
    channel_id discord_snowflake NOT NULL,
    author_id  discord_snowflake NOT NULL,
    day        date              NOT NULL,
    count      integer           NOT NULL CHECK (count > 0),
    PRIMARY KEY (channel_id, author_id, day)
);
CREATE INDEX message_count_day_idx ON message_count (day);

-- Single row. The history import only counts messages from before counting_since.
CREATE TABLE leaderboard_state
(
    id                  integer     PRIMARY KEY CHECK (id = 1),
    counting_since      timestamptz NOT NULL,
    history_imported_at timestamptz
);
