-- Members of a course. While the course has no channel they are waiting for one, afterwards
-- they see its channel.
CREATE TABLE course_member
(
    course_id bigint            NOT NULL REFERENCES course (id),
    user_id   discord_snowflake NOT NULL,
    joined_at timestamptz       NOT NULL DEFAULT now(),
    PRIMARY KEY (course_id, user_id)
);

CREATE INDEX course_member_user_idx ON course_member (user_id);

-- The message in the interest channel asking for a course's channel, until the channel opens.
CREATE TABLE course_request
(
    course_id  bigint            PRIMARY KEY REFERENCES course (id),
    channel_id discord_snowflake NOT NULL,
    message_id discord_snowflake NOT NULL
);
