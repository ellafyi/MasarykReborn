/// The time periods statistics can cover, as stored ("all") and as shown ("All time").
module Masaryk.Core.Services.Shared.Period

open Discord
open Masaryk.Core

let choices =
    [
        "all", "All time"
        "year", "This year"
        "month", "This month"
        "week", "This week"
    ]

let label (period: string) =
    choices |> List.find (fst >> (=) period) |> snd

let option () =
    let period =
        Options.option "period" "Time period (default: all time)" ApplicationCommandOptionType.String

    for value, name in choices do
        period.AddChoice(name, value) |> ignore

    period

/// The period the user picked with `option`, or all time.
let read (value: string -> obj option) =
    value "period" |> Option.map string |> Option.defaultValue (fst choices.Head)
