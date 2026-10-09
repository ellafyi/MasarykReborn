module Masaryk.Core.Starboard.Events

open System.Threading.Tasks
open Masaryk.Core.Starboard

type ReactionEvent =
    {
        ChannelId: uint64
        MessageId: uint64
        Emoji: EmojiKey
    }

/// Evaluate each reaction event in order. Log failures and continue with the next event.
let start (evaluate: ReactionEvent -> Task) : MailboxProcessor<ReactionEvent> =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () : Async<unit> =
            async {
                let! reaction = inbox.Receive()

                try
                    do! evaluate reaction |> Async.AwaitTask
                with error ->
                    eprintfn $"Starboard evaluation failed for {reaction.ChannelId}/{reaction.MessageId}: {error}"

                return! loop ()
            }

        loop ())
