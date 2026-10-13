namespace Masaryk.Core.Services.Starboard

[<RequireQualifiedAccess>]
type EmojiKey =
    | Unicode of string
    | Custom of uint64

type Emoji = { Key: EmojiKey; DisplayName: string }

[<RequireQualifiedAccess>]
type SourceMode =
    | Whitelist
    | Blacklist

type BoardSettings =
    {
        Name: string
        Mode: SourceMode
        Channels: Set<uint64>
        Target: uint64
        Threshold: int
        IsActive: bool
    }

type Board = { Id: int64; Settings: BoardSettings }

[<RequireQualifiedAccess>]
type SourceChannel =
    | Channel of channelId: uint64
    | Thread of channelId: uint64 * parentChannelId: uint64

type Reactor = { UserId: uint64; IsBot: bool }

type ReactionObservation =
    { Emoji: Emoji; Reactors: Reactor list }

type QualifyingReaction = { Emoji: Emoji; EligibleCount: int }
