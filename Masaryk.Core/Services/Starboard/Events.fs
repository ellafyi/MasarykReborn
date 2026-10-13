module Masaryk.Core.Services.Starboard.Events

open Masaryk.Core.Services.Starboard

type ReactionEvent =
    {
        ChannelId: uint64
        MessageId: uint64
        Emoji: EmojiKey
    }
