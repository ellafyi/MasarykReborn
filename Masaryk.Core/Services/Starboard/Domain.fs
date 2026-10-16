namespace Masaryk.Core.Services.Starboard

[<RequireQualifiedAccess>]
type EmojiKey =
    | Unicode of string
    | Custom of uint64

/// Text is how Discord writes the emoji: ⭐ or <:name:id>.
type Emoji = { Key: EmojiKey; Text: string }

[<RequireQualifiedAccess>]
type SourceMode =
    | Whitelist
    | Blacklist

type Board =
    {
        Name: string
        Mode: SourceMode
        Channels: Set<uint64>
        Target: uint64
        Threshold: int
        IsActive: bool
    }

type Reactor = { UserId: uint64; IsBot: bool }

type ReactionObservation =
    { Emoji: Emoji; Reactors: Reactor list }

type QualifyingReaction = { Emoji: Emoji; EligibleCount: int }
