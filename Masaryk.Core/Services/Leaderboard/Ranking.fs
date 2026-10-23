module Masaryk.Core.Services.Leaderboard.Ranking

open Discord
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

/// How many places the rankings list.
let top = 10

/// How many places either side of the member their position shows.
let around = 2

let private medal (position: int64) =
    match position with
    | 1L -> " 🥇"
    | 2L -> " 🥈"
    | 3L -> " 🥉"
    | _ -> ""

let private table (name: uint64 -> string) (highlight: uint64 option) (rows: LeaderboardRow list) : string =
    if rows.IsEmpty then
        "No messages yet."
    else
        let width = rows |> List.map (fun row -> (string row.Messages).Length) |> List.max

        rows
        |> List.map (fun row ->
            let shown =
                if Some row.Id = highlight then
                    $"**{name row.Id}**"
                else
                    name row.Id

            $"`{row.Position:D2}.` `{(string row.Messages).PadLeft width}` {shown}{medal row.Position}")
        |> String.concat "\n"

/// Mentions inside embeds show current server names and never ping.
let users (period: string) (channelId: uint64 option) (memberId: uint64) (rows: LeaderboardRow list) : Embed =
    let mention id = $"<@{id}>"

    let position =
        match rows |> List.tryFind (fun row -> row.Id = memberId) with
        | Some position ->
            rows
            |> List.filter (fun row -> abs (row.Position - position.Position) <= int64 around)
        | None -> []

    let embed =
        EmbedBuilder()
            .WithTitle($"Leaderboard · {Period.label period}")
            .WithColor(Color.Blue)
            .AddField(
                $"Top {top}",
                table mention (Some memberId) (rows |> List.filter (fun row -> row.Position <= int64 top))
            )
            .AddField("Position", table mention (Some memberId) position)

    channelId
    |> Option.iter (fun id -> embed.WithDescription($"In <#{id}>") |> ignore)

    embed.Build()

let channels (period: string) (rows: LeaderboardRow list) : Embed =
    EmbedBuilder()
        .WithTitle($"Most active channels · {Period.label period}")
        .WithColor(Color.Blue)
        .WithDescription(table (fun id -> $"<#{id}>") None rows)
        .Build()
