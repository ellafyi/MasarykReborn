/// Record what the old bot already put on its starboards, so this bot doesn't post those
/// messages again. Only reads the channel.
module Masaryk.Core.Services.Starboard.Import

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Discord
open Masaryk.Core.Services.Shared
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

/// Read the whole channel, newest first, and record every old post's original as posted on
/// the board in `boardChannel`. `progress` hears the running totals after every page.
let read
    (repository: StarboardRepository)
    (guildId: uint64)
    (channel: IMessageChannel)
    (boardChannel: uint64)
    (progress: Result -> Task)
    : Task<Result> =
    channel.GetMessagesAsync(Int32.MaxValue, options = Paging.patient ())
    |> Paging.fold
        (fun result page ->
            task {
                let posts =
                    page
                    |> Seq.filter _.Author.IsBot
                    |> Seq.choose (fun post ->
                        post.Embeds
                        |> Seq.tryPick (fun embed -> Option.ofObj embed.Description |> Option.bind (original guildId))
                        |> Option.map (fun source -> source, post.Id))
                    |> Seq.toList

                let! added = repository.SaveLegacy(boardChannel, posts)

                let result =
                    {
                        Read = result.Read + page.Count
                        Found = result.Found + posts.Length
                        Added = result.Added + added
                    }

                do! progress result
                return result, true
            })
        { Read = 0; Found = 0; Added = 0 }
