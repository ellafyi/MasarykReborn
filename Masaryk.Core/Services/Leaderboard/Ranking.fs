module Masaryk.Core.Services.Leaderboard.Ranking

open Discord
open Masaryk.Postgres

let periods =
    [
        "all", "All time"
        "year", "This year"
        "month", "This month"
        "week", "This week"
    ]

let private label (period: string) =
    periods |> List.find (fst >> (=) period) |> snd

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

    let around =
        match rows |> List.tryFind (fun row -> row.Id = memberId) with
        | Some position -> rows |> List.filter (fun row -> abs (row.Position - position.Position) <= 2L)
        | None -> []

    let embed =
        EmbedBuilder()
            .WithTitle($"Leaderboard · {label period}")
            .WithColor(Color.Blue)
            .AddField("Top 10", table mention (Some memberId) (rows |> List.filter (fun row -> row.Position <= 10L)))
            .AddField("Position", table mention (Some memberId) around)

    channelId
    |> Option.iter (fun id -> embed.WithDescription($"In <#{id}>") |> ignore)

    embed.Build()

let channels (period: string) (rows: LeaderboardRow list) : Embed =
    EmbedBuilder()
        .WithTitle($"Most active channels · {label period}")
        .WithColor(Color.Blue)
        .WithDescription(table (fun id -> $"<#{id}>") None rows)
        .Build()
