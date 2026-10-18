-- Posts the old bot made have no emoji or count we know, and can't be edited by this bot.
ALTER TABLE starboard_post
    ALTER COLUMN emoji DROP NOT NULL,
    ALTER COLUMN count DROP NOT NULL,
    ADD CHECK ((emoji IS NULL) = (count IS NULL));
