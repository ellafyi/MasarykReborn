CREATE TABLE guild_settings
(
    guild_id       discord_snowflake PRIMARY KEY,
    -- Where the bot reports progress and problems to the server's admins.
    log_channel_id discord_snowflake
);
