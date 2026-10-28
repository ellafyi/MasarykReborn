-- Finds each member's latest message for the course cleanup.
CREATE INDEX message_count_author_day_idx ON message_count (author_id, day);

-- Single row. The automatic course cleanup waits until an admin has looked at /courses prune.
CREATE TABLE course_prune_state
(
    id          integer     PRIMARY KEY CHECK (id = 1),
    reviewed_at timestamptz NOT NULL
);
