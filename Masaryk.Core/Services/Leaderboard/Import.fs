/// One-off import of the old bot's history: `import-history` after restoring its dump into the legacy schema.
module Masaryk.Core.Services.Leaderboard.Import

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Discord
open Discord.Net
open Discord.Rest
open Npgsql
open Masaryk.Core.Services.Shared
open Masaryk.Core.Services.Shared.Paging
open Masaryk.Postgres

/// Run work on up to 8 items at once.
let private inParallel (items: 'T list) (work: 'T -> Task<'R option>) : Task<'R list> =
    task {
        let results = ConcurrentBag<'R>()

        do!
            Parallel.ForEachAsync(
                items,
                ParallelOptions(MaxDegreeOfParallelism = 8),
                fun item _ ->
                    ValueTask(
                        task {
                            match! work item with
                            | Some result -> results.Add result
                            | None -> ()
                        }
                    )
            )

        return List.ofSeq results
    }

/// The dump has no bot flag, so ask Discord. User lookups share one slow rate limit, so one at a time,
/// saving each result so a rerun can skip it.
let private checkAuthors (client: DiscordRestClient) (import: LegacyImport) (authors: uint64 list) =
    task {
        for index, id in List.indexed authors do
            let! user = client.GetUserAsync(id, patient ())
            do! import.SaveAuthor(id, not (isNull user) && user.IsBot)

            if (index + 1) % 100 = 0 then
                printfn $"Checked {index + 1} of {authors.Length} authors."
    }

/// Old thread messages count toward the thread's parent channel.
let private parents (client: DiscordRestClient) (channels: uint64 list) =
    inParallel channels (fun id ->
        task {
            try
                match! client.GetChannelAsync(id, patient ()) with
                | :? RestThreadChannel as thread -> return Some(id, thread.ParentChannelId)
                | _ -> return None
            // Channels the bot can't see keep their own ID.
            with :? HttpException ->
                return None
        })

/// Active threads, and archived ones that may have messages after `since`.
let private threads (container: IThreadContainerChannel) (since: DateTimeOffset) : Task<IThreadChannel list> =
    task {
        let! active = container.GetActiveThreadsAsync(patient ())
        let archived = ResizeArray<IThreadChannel>()
        let mutable before = Nullable()
        let mutable reading = true

        while reading do
            let! page = container.GetPublicArchivedThreadsAsync(100, before, patient ())

            let recent =
                page
                |> Seq.filter (fun thread -> thread.ArchiveTimestamp >= since)
                |> Seq.toList

            archived.AddRange recent
            reading <- page.Count = 100 && recent.Length = page.Count

            if reading then
                before <- Nullable(page |> Seq.map _.ArchiveTimestamp |> Seq.min)

        return List.ofSeq (Seq.cast<IThreadChannel> active) @ List.ofSeq archived
    }

/// Save the counted messages between `start` and `cutoff`, under `countedChannel`.
let private fillGap
    (import: LegacyImport)
    (guildId: uint64)
    (channel: IMessageChannel)
    (countedChannel: uint64)
    (start: uint64)
    (cutoff: uint64)
    : Task<int> =
    channel.GetMessagesAsync(start, Direction.After, Int32.MaxValue, options = patient ())
    |> Paging.fold
        (fun saved page ->
            task {
                let counted =
                    page
                    |> Seq.filter (fun message -> message.Id < cutoff && Counting.counts guildId message)
                    |> Seq.map (fun message ->
                        message.Id, countedChannel, message.Author.Id, message.Timestamp.UtcDateTime)
                    |> Seq.toList

                do! import.SaveGap counted
                return saved + counted.Length, page |> Seq.forall (fun message -> message.Id < cutoff)
            })
        0

let private gap (client: DiscordRestClient) (import: LegacyImport) (guildId: uint64) (countingSince: DateTime) =
    task {
        let! last = import.LastMessages()
        let! dumpEnd = import.DumpEnd()
        let dumpEnd = DateTimeOffset(dumpEnd)
        let cutoff = SnowflakeUtils.ToSnowflake(DateTimeOffset(countingSince))

        let startOf id =
            last
            |> Map.tryFind id
            |> Option.defaultValue (SnowflakeUtils.ToSnowflake dumpEnd)

        let! guild = client.GetGuildAsync(guildId, patient ())
        let! channels = guild.GetChannelsAsync(patient ())
        let mutable sources = []

        for channel in channels do
            match channel :> IChannel with
            | :? IMessageChannel as messages -> sources <- (messages, channel.Id) :: sources
            | _ -> ()

            match channel :> IChannel with
            // Discord.Net treats voice channels as text channels, but they can't have threads.
            | :? IVoiceChannel -> ()
            | :? IThreadContainerChannel as container ->
                let! inside = threads container dumpEnd

                for thread in inside do
                    sources <- (thread :> IMessageChannel, channel.Id) :: sources
            | _ -> ()

        printfn $"Gap: reading {sources.Length} channels and threads from {dumpEnd:u} to {countingSince:u}."

        let! counts =
            inParallel sources (fun (source, countedChannel) ->
                task {
                    try
                        let! saved = fillGap import guildId source countedChannel (startOf source.Id) cutoff
                        return Some saved
                    with :? HttpException as error ->
                        printfn $"Gap: skipped {source.Name}: {error.Reason}"
                        return None
                })

        printfn $"Gap: saved {List.sum counts} messages."
    }

let run (database: NpgsqlDataSource) (guildId: uint64) (token: string) : Task<unit> =
    task {
        let import = LegacyImport(database)
        let! countingSince = LeaderboardRepository(database).StartCounting()
        let! imported = import.Imported()

        if imported then
            printfn "The history has already been imported."
        else
            use client = new DiscordRestClient()
            do! client.LoginAsync(TokenType.Bot, token)
            do! import.Prepare()

            // Bots with fewer messages barely affect the rankings and would take hours to check.
            let! authors = import.BusyAuthors 100
            printfn $"Checking {authors.Length} authors with 100+ messages for bots."
            do! checkAuthors client import authors

            let! channels = import.Channels()
            printfn $"Looking up {channels.Length} old channels."
            let! threads = parents client channels
            do! import.SaveParents threads
            printfn $"Found {threads.Length} threads."

            do! gap client import guildId countingSince

            let! added = import.Finish()
            printfn $"Imported {added} messages."
    }
