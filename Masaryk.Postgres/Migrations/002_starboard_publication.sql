ALTER TABLE starboard_entry
    DROP CONSTRAINT starboard_entry_archive_status_check,
    ADD CONSTRAINT starboard_entry_archive_status_check
        CHECK (archive_status IN ('pending', 'sending', 'failed', 'uncertain', 'posted')),
    ADD COLUMN publication_channel_id discord_snowflake,
    ADD COLUMN publication_started_at timestamptz,
    ADD COLUMN publication_error text;
