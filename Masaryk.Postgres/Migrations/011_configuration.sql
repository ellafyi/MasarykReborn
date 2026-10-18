-- Every server setting in one document (format: Masaryk.Configuration). Saving adds a
-- revision; the newest one per guild is in use.
CREATE TABLE configuration_revision
(
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guild_id discord_snowflake NOT NULL,
    document jsonb             NOT NULL,
    -- Null when a migration made the revision.
    saved_by discord_snowflake,
    saved_at timestamptz       NOT NULL DEFAULT now()
);
CREATE INDEX configuration_revision_guild_idx ON configuration_revision (guild_id, id DESC);

-- The bot listens on this channel and reloads, whoever saved.
CREATE FUNCTION notify_configuration_saved() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    PERFORM pg_notify('configuration', NEW.guild_id::text);
    RETURN NEW;
END
$$;
CREATE TRIGGER configuration_saved
    AFTER INSERT
    ON configuration_revision
    FOR EACH ROW
EXECUTE FUNCTION notify_configuration_saved();

-- Carry over the starboard boards and the log channel; the rest keeps its defaults.
INSERT INTO configuration_revision (guild_id, document)
SELECT g.guild_id,
       jsonb_build_object(
               'logChannel', (SELECT s.log_channel_id::text FROM guild_settings s WHERE s.guild_id = g.guild_id),
               'starboard', jsonb_build_object('boards', coalesce(
                       (SELECT jsonb_agg(jsonb_build_object(
                               'name', b ->> 'name',
                               'target', b ->> 'target',
                               'mode', b ->> 'mode',
                               'channels', coalesce((SELECT jsonb_agg(channel #>> '{}')
                                                     FROM jsonb_array_elements(b -> 'channels') channel),
                                                    '[]'::jsonb),
                               'threshold', (b ->> 'threshold')::int,
                               'active', coalesce((b ->> 'active')::boolean, true)))
                        FROM jsonb_array_elements(sc.configuration) b),
                       '[]'::jsonb)))
FROM (SELECT guild_id FROM starboard_configuration UNION SELECT guild_id FROM guild_settings) g
         LEFT JOIN starboard_configuration sc ON sc.guild_id = g.guild_id;

DROP TABLE starboard_configuration, guild_settings, course_category_override, family_category_override;
ALTER TABLE faculty DROP COLUMN sync_enabled;
