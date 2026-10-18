module Masaryk.Core.Services.Shared.Paging

open System
open System.Collections.Generic
open System.Threading.Tasks
open Discord

/// Discord.Net gives up on requests that wait more than 15 s for a rate limit; Discord makes them wait ~30 s.
/// (No timeout at all crashes Discord.Net's rate limiter.)
let patient () =
    RequestOptions(Timeout = Nullable 300_000)

/// Fold over Discord.Net's pages until they run out or `step` returns false.
let fold (step: 'S -> 'Page -> Task<'S * bool>) (state: 'S) (pages: IAsyncEnumerable<'Page>) : Task<'S> =
    task {
        use pages = pages.GetAsyncEnumerator()
        let mutable state = state
        let mutable reading = true

        while reading do
            let! next = pages.MoveNextAsync()

            if next then
                let! after, more = step state pages.Current
                state <- after
                reading <- more
            else
                reading <- false

        return state
    }
