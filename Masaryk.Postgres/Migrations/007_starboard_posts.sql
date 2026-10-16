-- Starboard posts are rendered from the live message; only what prevents reposting
-- and lets the bot update the posted count is kept.
CREATE TABLE starboard_post
(
    source_message_id discord_snowflake PRIMARY KEY,
    target_channel_id discord_snowflake NOT NULL,
    post_message_id   discord_snowflake NOT NULL,
    emoji             text              NOT NULL,
    count             integer           NOT NULL
);

INSERT INTO starboard_post
SELECT e.source_message_id, s.destination_channel_id, e.archive_message_id,
       CASE WHEN e.qualifying_emoji_key LIKE 'custom:%'
            THEN '<:' || e.qualifying_emoji_name || ':' || substring(e.qualifying_emoji_key FROM 8) || '>'
            ELSE e.qualifying_emoji_name END,
       e.qualifying_count
FROM starboard_entry e
JOIN starboard s ON s.id = e.starboard_id
WHERE e.archive_message_id IS NOT NULL;

DROP TABLE entry_reply, entry_embed, entry_attachment, entry_reaction, starboard_entry, source_channel, starboard;
