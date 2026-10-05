namespace Masaryk.Core.Starboard

[<RequireQualifiedAccess>]
type EmojiKey =
    | Unicode of string
    | Custom of uint64

type Emoji = { Key: EmojiKey; DisplayName: string }

type Board =
    { Id: int64
      DestinationChannelId: uint64
      Threshold: int
      IsActive: bool }

[<RequireQualifiedAccess>]
type SourceChannel =
    | Channel of channelId: uint64
    | Thread of channelId: uint64 * parentChannelId: uint64

type Reactor = { UserId: uint64; IsBot: bool }

type ReactionObservation =
    { Emoji: Emoji; Reactors: Reactor list }

type QualifyingReaction = { Emoji: Emoji; EligibleCount: int }
