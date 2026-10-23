module Masaryk.Core.Services.Menu.Rendering

open System
open System.Globalization
open Discord
open Masaryk.Core.Services.Menu.Fetching
open Masaryk.Core.Services.Shared

let prefix = "menu"

// Discord allows 10 embeds per message and 6,000 characters of embed text in total. Each menu's
// text is shortened to 1,000 characters (whole dishes, then "…") so at least five restaurants fit
// on a page.
let private maxEmbeds = 10
let private maxText = 6000
let private limit = 1000

let private price (value: decimal) =
    value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') + " Kč"

/// Whole lines up to the limit, then an ellipsis.
let private fit (lines: string list) =
    let rec take used kept lines =
        match lines with
        | [] -> List.rev kept
        | (line: string) :: rest when used + line.Length + 1 <= limit ->
            take (used + line.Length + 1) (line :: kept) rest
        | _ -> List.rev ("…" :: kept)

    take 0 [] lines |> String.concat "\n"

let private describe (menu: Menu) : string =
    match menu with
    | Items [] -> "No menu today."
    | Items items ->
        items
        |> List.map (fun item ->
            let text = Format.Sanitize item.Text

            match item.Price with
            | Some value -> $"- {text} · *{price value}*"
            | None -> $"- {text}")
        |> fit
    | Unavailable -> "Couldn't load the menu."
    | LinkOnly -> "No menu online."

let private pages (embeds: Embed list) : Embed list list =
    let size (embed: Embed) =
        embed.Title.Length + embed.Description.Length

    embeds
    |> List.fold
        (fun pages embed ->
            match pages with
            | current :: rest when
                List.length current < maxEmbeds
                && List.sumBy size current + size embed <= maxText
                ->
                (current @ [ embed ]) :: rest
            | _ -> [ embed ] :: pages)
        []
    |> List.rev

let render (menus: Menus) (page: int) : string * Embed array * MessageComponent =
    let pages =
        menus.Menus
        |> List.map (fun (restaurant, menu) ->
            EmbedBuilder()
                .WithTitle($"{restaurant.Icon} {restaurant.Name}")
                .WithUrl(restaurant.Url)
                .WithColor(Color(restaurant.Color))
                .WithDescription(describe menu)
                .Build())
        |> pages

    let count = max 1 pages.Length
    let page = Math.Clamp(page, 0, count - 1)

    let header =
        if menus.Menus.IsEmpty then
            "No restaurants are set up."
        else
            $"**Lunch near FI, {menus.Day.Day}. {menus.Day.Month}. {menus.Day.Year}** · page {page + 1} of {count}"

    let embeds = pages |> List.tryItem page |> Option.defaultValue [] |> List.toArray
    header, embeds, Pager.buttons prefix "" page count
