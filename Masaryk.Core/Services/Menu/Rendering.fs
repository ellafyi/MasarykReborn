module Masaryk.Core.Services.Menu.Rendering

open System
open System.Globalization
open Discord
open Masaryk.Core.Services.Menu.Fetching
open Masaryk.Core.Services.Shared

let pageSize = 1
let private limit = 4000

let buttonId (page: int) = $"menu:{page}"

let parseButton (id: string) : int option =
    match id.Split(':') with
    | [| "menu"; page |] -> Some(int page)
    | _ -> None

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

let describe (menu: Menu) : string =
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

let render (menus: Menus) (page: int) : string * Embed array * MessageComponent =
    let count = Pager.count pageSize (int64 menus.Menus.Length)
    let page = Math.Clamp(page, 0, count - 1)

    let embeds =
        menus.Menus
        |> List.skip (page * pageSize)
        |> List.truncate pageSize
        |> List.map (fun (restaurant, menu) ->
            EmbedBuilder()
                .WithTitle($"{restaurant.Icon} {restaurant.Name}")
                .WithUrl(restaurant.Url)
                .WithColor(Color(restaurant.Color))
                .WithDescription(describe menu)
                .Build())
        |> List.toArray

    let header =
        if menus.Menus.IsEmpty then
            "No restaurants are set up."
        else
            $"**Lunch near FI, {menus.Day.Day}. {menus.Day.Month}. {menus.Day.Year}** · restaurant {page + 1} of {count}"

    header, embeds, Pager.buttons buttonId page count
