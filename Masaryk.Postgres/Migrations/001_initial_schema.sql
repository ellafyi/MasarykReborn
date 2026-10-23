CREATE DOMAIN discord_snowflake AS numeric(20, 0);

CREATE TABLE starboard_configuration
(
    guild_id      discord_snowflake PRIMARY KEY,
    configuration jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE TABLE starboard
(
    id                     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guild_id               discord_snowflake NOT NULL,
    name                   text NOT NULL,
    destination_channel_id discord_snowflake NOT NULL,
    reaction_threshold     integer           NOT NULL CHECK (reaction_threshold > 0),
    is_active              boolean           NOT NULL DEFAULT true,
    created_at             timestamptz       NOT NULL DEFAULT now(),
    UNIQUE (guild_id, name)
);

CREATE TABLE source_channel
(
    discord_channel_id discord_snowflake PRIMARY KEY
);

CREATE TABLE starboard_entry
(
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    starboard_id         bigint            NOT NULL REFERENCES starboard (id),
    source_channel_id    discord_snowflake NOT NULL REFERENCES source_channel (discord_channel_id),
    source_message_id    discord_snowflake NOT NULL UNIQUE,
    source_created_at    timestamptz       NOT NULL,
    archive_message_id   discord_snowflake UNIQUE,
    author_user_id       discord_snowflake,
    author_display_name  text              NOT NULL,
    content_snapshot     text              NOT NULL,
    source_jump_url      text              NOT NULL,
    qualifying_emoji_key text              NOT NULL,
    qualifying_emoji_name text             NOT NULL,
    qualifying_count     integer           NOT NULL CHECK (qualifying_count > 0),
    archive_status       text              NOT NULL DEFAULT 'pending'
        CHECK (archive_status IN ('pending', 'failed', 'posted')),
    captured_at          timestamptz       NOT NULL DEFAULT now(),
    posted_at            timestamptz,
    source_deleted_at    timestamptz
);
CREATE INDEX starboard_entry_board_idx ON starboard_entry (starboard_id);
CREATE INDEX starboard_entry_source_channel_idx ON starboard_entry (source_channel_id);
CREATE TABLE entry_reaction
(
    entry_id       bigint  NOT NULL REFERENCES starboard_entry (id) ON DELETE CASCADE,
    emoji_key      text    NOT NULL,
    emoji_name     text    NOT NULL,
    count_snapshot integer NOT NULL CHECK (count_snapshot >= 0),
    PRIMARY KEY (entry_id, emoji_key)
);
CREATE TABLE entry_attachment
(
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entry_id   bigint  NOT NULL REFERENCES starboard_entry (id) ON DELETE CASCADE,
    position   integer NOT NULL CHECK (position >= 0),
    discord_attachment_id discord_snowflake NOT NULL,
    filename   text    NOT NULL,
    url        text    NOT NULL,
    media_type text,
    size_bytes bigint CHECK (size_bytes >= 0),
    UNIQUE (entry_id, position)
);
CREATE TABLE entry_embed
(
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entry_id bigint  NOT NULL REFERENCES starboard_entry (id) ON DELETE CASCADE,
    position integer NOT NULL CHECK (position >= 0),
    metadata jsonb   NOT NULL,
    UNIQUE (entry_id, position)
);

CREATE TABLE faculty
(
    code         text PRIMARY KEY,
    name         text,
    sync_enabled boolean NOT NULL DEFAULT true
);
CREATE TABLE catalog_import
(
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    faculty_code text        NOT NULL REFERENCES faculty (code),
    term         text        NOT NULL,
    source_label text,
    imported_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX catalog_import_scope_idx ON catalog_import (faculty_code, term, imported_at DESC);
CREATE TABLE course
(
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    faculty_code text NOT NULL REFERENCES faculty (code),
    code         text NOT NULL,
    UNIQUE (faculty_code, code)
);
CREATE TABLE course_offering
(
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    course_id bigint NOT NULL REFERENCES course (id),
    term      text   NOT NULL,
    UNIQUE (course_id, term)
);
CREATE TABLE course_revision
(
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    offering_id       bigint      NOT NULL REFERENCES course_offering (id),
    catalog_import_id bigint      NOT NULL REFERENCES catalog_import (id),
    present_in_export boolean     NOT NULL,
    name              text,
    description       text,
    created_at        timestamptz NOT NULL DEFAULT now(),
    UNIQUE (offering_id, catalog_import_id)
);
CREATE INDEX course_revision_import_idx ON course_revision (catalog_import_id);

CREATE TABLE proposal
(
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    course_id          bigint            NOT NULL REFERENCES course (id),
    course_revision_id bigint            NOT NULL REFERENCES course_revision (id),
    proposer_user_id   discord_snowflake NOT NULL,
    discord_channel_id discord_snowflake,
    discord_message_id discord_snowflake,
    status             text              NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'approved', 'rejected')),
    reviewer_user_id   discord_snowflake,
    decision_reason    text,
    created_at         timestamptz       NOT NULL DEFAULT now(),
    decided_at         timestamptz
);
CREATE UNIQUE INDEX proposal_one_open_per_course_idx ON proposal (course_id) WHERE status = 'open';
CREATE INDEX proposal_revision_idx ON proposal (course_revision_id);

CREATE TABLE proposal_support
(
    proposal_id       bigint            NOT NULL REFERENCES proposal (id) ON DELETE CASCADE,
    supporter_user_id discord_snowflake NOT NULL,
    created_at        timestamptz       NOT NULL DEFAULT now(),
    PRIMARY KEY (proposal_id, supporter_user_id)
);
CREATE TABLE course_channel
(
    course_id               bigint PRIMARY KEY REFERENCES course (id),
    approved_proposal_id    bigint      NOT NULL UNIQUE REFERENCES proposal (id),
    provisioning_status     text        NOT NULL DEFAULT 'pending'
        CHECK (provisioning_status IN ('pending', 'failed', 'created')),
    discord_channel_id      discord_snowflake UNIQUE,
    discord_category_id     discord_snowflake,
    category_name           text        NOT NULL,
    last_synced_revision_id bigint      REFERENCES course_revision (id),
    created_at              timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE authorized_role
(
    role_id    discord_snowflake NOT NULL,
    capability text              NOT NULL CHECK (capability IN ('participant', 'reviewer')),
    PRIMARY KEY (role_id, capability)
);
CREATE TABLE family_category_override
(
    course_prefix text PRIMARY KEY,
    category_name text NOT NULL
);
CREATE TABLE course_category_override
(
    course_id     bigint PRIMARY KEY REFERENCES course (id),
    category_name text NOT NULL
);
