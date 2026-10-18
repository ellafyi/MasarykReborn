module Masaryk.Core.Services.Starboard.Evaluation

open Masaryk.Configuration
open Masaryk.Core.Services.Starboard

/// The active board for a channel (threads pass their parent). Targets are never sources.
let resolveBoard (boards: Board list) (channelId: uint64) : Board option =
    if boards |> List.exists (fun board -> board.Target = channelId) then
        None
    else
        boards
        |> List.tryFind (fun board ->
            let listed = Set.contains channelId board.Channels

            match board.Mode with
            | SourceMode.Whitelist -> listed
            | SourceMode.Blacklist -> not listed)
        |> Option.filter _.IsActive

/// People who reacted, once each, not counting bots or the author.
let eligibleCount (authorId: uint64) (reactors: Reactor seq) : int =
    reactors
    |> Seq.filter (fun user -> not user.IsBot && user.UserId <> authorId)
    |> Seq.distinctBy _.UserId
    |> Seq.length

/// An emoji with enough eligible reactions, preferring the one just used.
let qualifyingReaction
    (threshold: int)
    (authorId: uint64)
    (preferred: EmojiKey)
    (reactions: ReactionObservation seq)
    : QualifyingReaction option =
    let qualifying =
        reactions
        |> Seq.groupBy _.Emoji.Key
        |> Seq.map (fun (_, observations) ->
            {
                Emoji = (Seq.head observations).Emoji
                EligibleCount = observations |> Seq.collect _.Reactors |> eligibleCount authorId
            })
        |> Seq.filter (fun reaction -> reaction.EligibleCount >= threshold)
        |> Seq.sortBy _.Emoji.Key
        |> Seq.toList

    qualifying
    |> List.tryFind (fun reaction -> reaction.Emoji.Key = preferred)
    |> Option.orElse (List.tryHead qualifying)
