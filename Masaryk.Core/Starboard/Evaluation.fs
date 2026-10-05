module Masaryk.Core.Starboard.Evaluation

open Masaryk.Core.Starboard

type Qualification =
    { Board: Board
      Reaction: QualifyingReaction }

let resolveBoard (assignments: Map<uint64, Board>) source =
    let channelId =
        match source with
        | SourceChannel.Channel id -> id
        | SourceChannel.Thread(_, parentId) -> parentId

    Map.tryFind channelId assignments

let eligibleCount authorId (reactors: Reactor seq) =
    reactors
    |> Seq.filter (fun user -> not user.IsBot && Some user.UserId <> authorId)
    |> Seq.map _.UserId
    |> Seq.distinct
    |> Seq.length

let qualifyingReaction threshold authorId preferredEmoji (reactions: ReactionObservation seq) =
    let qualifying =
        reactions
        |> Seq.groupBy (fun reaction -> reaction.Emoji.Key)
        |> Seq.map (fun (_, observations) ->
            let observations = Seq.toList observations

            { Emoji = observations.Head.Emoji
              EligibleCount = observations |> Seq.collect _.Reactors |> eligibleCount authorId })
        |> Seq.filter (fun reaction -> reaction.EligibleCount >= threshold)
        |> Seq.sortBy (fun reaction -> reaction.Emoji.Key)
        |> Seq.toList

    qualifying
    |> List.tryFind (fun reaction -> Some reaction.Emoji.Key = preferredEmoji)
    |> Option.orElseWith (fun () -> List.tryHead qualifying)

let evaluate assignments source authorId preferredEmoji reactions =
    resolveBoard assignments source
    |> Option.filter _.IsActive
    |> Option.bind (fun board ->
        qualifyingReaction board.Threshold authorId preferredEmoji reactions
        |> Option.map (fun reaction -> { Board = board; Reaction = reaction }))
