module Masaryk.Core.Services.Shared.Worker

open System.Threading.Tasks

/// Process queued items one at a time so gateway handlers never wait for REST calls.
/// Log failures with describe and continue with the next item.
let start (describe: 'T -> string) (handle: 'T -> Task) : MailboxProcessor<'T> =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () : Async<unit> =
            async {
                let! item = inbox.Receive()

                try
                    do! handle item |> Async.AwaitTask
                with error ->
                    eprintfn $"{describe item} failed: {error}"

                return! loop ()
            }

        loop ())
