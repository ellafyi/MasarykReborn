-- Admins create course channels directly, so the proposal workflow goes.
DROP TABLE course_channel, proposal_support, proposal, authorized_role;

CREATE TABLE course_channel
(
    course_id  bigint            PRIMARY KEY REFERENCES course (id),
    channel_id discord_snowflake NOT NULL UNIQUE,
    linked_at  timestamptz       NOT NULL DEFAULT now()
);
