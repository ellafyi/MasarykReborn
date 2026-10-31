-- When each daily job last ran, so restarting the bot doesn't run it again.
CREATE TABLE scheduled_job
(
    name     text        PRIMARY KEY,
    last_run timestamptz NOT NULL
);
