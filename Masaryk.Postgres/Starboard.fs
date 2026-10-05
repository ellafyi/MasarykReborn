module Masaryk.Postgres.Starboard

open System

type BoardConfiguration =
    {
        Name: string
        Target: uint64
        Threshold: int
        IsActive: bool
    }

type ReactionSnapshot =
    {
        EmojiKey: string
        EmojiName: string
        RawCount: int
    }

type AttachmentSnapshot =
    {
        Id: uint64
        Filename: string
        Url: string
        MediaType: string option
        SizeBytes: int64 option
    }

type MessageSnapshot =
    {
        BoardId: int64
        SourceChannelId: uint64
        SourceMessageId: uint64
        SourceCreatedAt: DateTime
        AuthorUserId: uint64 option
        AuthorDisplayName: string
        Content: string
        SourceJumpUrl: string
        QualifyingEmojiKey: string
        QualifyingEmojiName: string
        QualifyingCount: int
        Reactions: ReactionSnapshot list
        Attachments: AttachmentSnapshot list
        Embeds: string list
    }

type Entry =
    {
        Id: int64
        CapturedAt: DateTime
        Snapshot: MessageSnapshot
    }
