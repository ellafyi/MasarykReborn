-- A locked-out member's own channel overwrites that let them see a channel. Discord applies them
-- after role overwrites, so the lockout takes them away and puts them back at the end.
CREATE TABLE lockout_overwrite
(
    user_id     discord_snowflake NOT NULL REFERENCES lockout (user_id) ON DELETE CASCADE,
    channel_id  discord_snowflake NOT NULL,
    allow_value numeric(20, 0)    NOT NULL,
    deny_value  numeric(20, 0)    NOT NULL,
    PRIMARY KEY (user_id, channel_id)
);
