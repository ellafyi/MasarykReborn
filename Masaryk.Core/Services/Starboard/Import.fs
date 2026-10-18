/// Record what the old bot already put on its starboards, so this bot doesn't post those
/// messages again. Only reads the channel.
module Masaryk.Core.Services.Starboard.Import

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Threading.Tasks
open Discord
open Masaryk.Postgres

/// The old bot ended every post's description with `[Jump to original!](<link>) in <#channel>`.
let private jump =
    Regex(@"\[Jump to original!\]\(https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/channels/(\d+)/(\d+)/(\d+)\)")

/// The original message an old starboard post points to, if it is in this guild.
/// The last link counts: the quoted content above it could contain the same text.
let original (guildId: uint64) (description: string) : uint64 option =
    jump.Matches(description)
    |> Seq.tryLast
    |> Option.filter (fun m -> uint64 m.Groups[1].Value = guildId)
    |> Option.map (fun m -> uint64 m.Groups[3].Value)

type Result = { Read: int; Found: int; Added: int }

/// Discord.Net gives up on requests that wait more than 15 s for a rate limit.
let private patient () =
    RequestOptions(Timeout = Nullable 300_000)

/// Read the whole channel, newest first, and record every old post's original as posted on
/// the board in `boardChannel`. `progress` hears the running totals after every page.
let read
    (repository: StarboardRepository)
    (guildId: uint64)
    (channel: IMessageChannel)
    (boardChannel: uint64)
    (progress: Result -> Task)
    : Task<Result> =
    task {
        let mutable before: uint64 option = None
        let mutable reading = true
        let mutable result = { Read = 0; Found = 0; Added = 0 }

        while reading do
            let pages =
                match before with
                | Some id -> channel.GetMessagesAsync(id, Direction.Before, 100, options = patient ())
                | None -> channel.GetMessagesAsync(100, options = patient ())

            let! page = unbox<IAsyncEnumerable<seq<IMessage>>>(box pages) |> _.FlattenAsync()
            let page = List.ofSeq page

            let posts =
                page
                |> List.filter _.Author.IsBot
                |> List.choose (fun post ->
                    post.Embeds
                    |> Seq.tryPick (fun embed ->
                        original guildId (Option.ofObj embed.Description |> Option.defaultValue ""))
                    |> Option.map (fun source -> source, boardChannel, post.Id))

            let! added = repository.SaveLegacy posts

            result <-
                {
                    Read = result.Read + page.Length
                    Found = result.Found + posts.Length
                    Added = result.Added + added
                }

            do! progress result
            reading <- page.Length = 100

            if reading then
                before <- Some(page |> List.map _.Id |> List.min)

        return result
    }
