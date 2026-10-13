module Masaryk.Core.Services.Starboard.Evaluation

open Masaryk.Core.Services.Starboard

type Qualification =
    {
        Board: Board
        Reaction: QualifyingReaction
    }

let resolveBoard (boards: Board list) (source: SourceChannel) : Board option =
    let channelId =
        match source with
        | SourceChannel.Channel id -> id
        | SourceChannel.Thread(_, parentId) -> parentId

    if boards |> List.exists (fun board -> board.Settings.Target = channelId) then
        None
    else
        boards
        |> List.tryFind (fun board ->
            let listed = Set.contains channelId board.Settings.Channels

            match board.Settings.Mode with
            | SourceMode.Whitelist -> listed
            | SourceMode.Blacklist -> not listed)

let eligibleCount (authorId: uint64 option) (reactors: Reactor seq) : int =
    reactors
    |> Seq.filter (fun user -> not user.IsBot && Some user.UserId <> authorId)
    |> Seq.map _.UserId
    |> Seq.distinct
    |> Seq.length

let qualifyingReaction
    (threshold: int)
    (authorId: uint64 option)
    (preferredEmoji: EmojiKey option)
    (reactions: ReactionObservation seq)
    : QualifyingReaction option =
    let qualifying =
        reactions
        |> Seq.groupBy (fun reaction -> reaction.Emoji.Key)
        |> Seq.map (fun (_, observations) ->
            let observations = Seq.toList observations

            {
                Emoji = observations.Head.Emoji
                EligibleCount = observations |> Seq.collect _.Reactors |> eligibleCount authorId
            })
        |> Seq.filter (fun reaction -> reaction.EligibleCount >= threshold)
        |> Seq.sortBy (fun reaction -> reaction.Emoji.Key)
        |> Seq.toList

    qualifying
    |> List.tryFind (fun reaction -> Some reaction.Emoji.Key = preferredEmoji)
    |> Option.orElseWith (fun () -> List.tryHead qualifying)

let evaluate
    (boards: Board list)
    (source: SourceChannel)
    (authorId: uint64 option)
    (preferredEmoji: EmojiKey option)
    (reactions: ReactionObservation seq)
    : Qualification option =
    resolveBoard boards source
    |> Option.filter _.Settings.IsActive
    |> Option.bind (fun board ->
        qualifyingReaction board.Settings.Threshold authorId preferredEmoji reactions
        |> Option.map (fun reaction -> { Board = board; Reaction = reaction }))
