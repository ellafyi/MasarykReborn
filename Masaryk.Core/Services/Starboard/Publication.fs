module Masaryk.Core.Services.Starboard.Publication

open System
open System.Threading.Tasks
open Discord
open Discord.Net
open Discord.WebSocket
open Masaryk.Postgres
open Masaryk.Postgres.Starboard

/// Send once. Retry rate-limit rejections, but leave timeouts and server failures uncertain.
let send (channel: IMessageChannel) (post: Rendering.Post) : Task<PublicationResult> =
    task {
        try
            let! message =
                channel.SendMessageAsync(
                    embeds = post.Embeds,
                    components = post.Components,
                    allowedMentions = AllowedMentions.None,
                    options = RequestOptions(RetryMode = RetryMode.RetryRatelimit)
                )

            return PublicationResult.Posted message.Id
        with
        | :? HttpException as error when
            int error.HttpCode >= 400
            && int error.HttpCode < 500
            && int error.HttpCode <> 408
            ->
            return PublicationResult.Failed error.Message
        | :? RateLimitedException as error -> return PublicationResult.Failed error.Message
        | :? ArgumentException as error -> return PublicationResult.Failed error.Message
        | error -> return PublicationResult.Uncertain error.Message
    }

/// Record the attempt before sending, then save its outcome separately from the snapshot.
let publish
    (repository: StarboardRepository)
    (client: DiscordSocketClient)
    (target: uint64)
    (entry: Entry)
    : Task<unit> =
    task {
        let! started = repository.BeginPublication(entry.Id, target)

        if started then
            let! result =
                task {
                    try
                        let post = Rendering.create entry
                        let! channel = client.Rest.GetChannelAsync(target)
                        return! send ((channel :> IChannel) :?> IMessageChannel) post
                    with error ->
                        // Rendering and destination lookup happen before any message send.
                        return PublicationResult.Failed error.Message
                }

            // If this write fails after Discord accepts the post, keep the attempt as sending.
            do! repository.RecordPublication(entry.Id, result)

            match result with
            | PublicationResult.Posted id -> printfn $"Starboard entry {entry.Id}: posted message {id} in {target}."
            | PublicationResult.Failed error -> eprintfn $"Starboard entry {entry.Id}: publication failed: {error}"
            | PublicationResult.Uncertain error ->
                eprintfn $"Starboard entry {entry.Id}: publication uncertain: {error}"
    }
