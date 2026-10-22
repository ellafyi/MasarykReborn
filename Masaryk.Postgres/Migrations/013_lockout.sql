-- Members locked out with /touch-grass. A row is deleted once the time is up.
CREATE TABLE lockout
(
    user_id      discord_snowflake PRIMARY KEY,
    -- Kept so the end removes it even if the configuration changed.
    lock_role_id discord_snowflake NOT NULL,
    -- The roles taken away.
    role_ids     numeric(20, 0)[]  NOT NULL,
    locked_at    timestamptz       NOT NULL DEFAULT now(),
    locked_until timestamptz       NOT NULL
);

CREATE INDEX lockout_until_idx ON lockout (locked_until);
