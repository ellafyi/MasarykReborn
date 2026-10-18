/// Turn each source's page or API response into today's menu items.
module Masaryk.Core.Services.Menu.Parsing

open System
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Masaryk.Core.Services.Shared

type Item = { Text: string; Price: decimal option }

let private options = RegexOptions.Singleline ||| RegexOptions.IgnoreCase

/// Leading `1)`, `M2:`, `Pondělí:` and similar numbering.
let private numbering =
    Regex(
        @"^((([Mm]?\d+)|([Pp]o(nd[eě]l[ií])?)|([UuÚú]t(er[yý])?)|([Ss]t([rř]eda)?)|([ČčCc]t(vrtek)?)|([Pp][aá](tek)?))[):,;.]\s*)+"
    )

/// Dot runs and ellipses Táo uses as separators.
let private dots = Regex(@"\s*(\.{2,}|…+)[.…]*\s*")

let private withoutEmoji (text: string) =
    text.EnumerateRunes()
    |> Seq.filter (fun rune ->
        Rune.GetUnicodeCategory rune <> UnicodeCategory.OtherSymbol
        && rune.Value <> 0xFE0F)
    |> Seq.map string
    |> String.concat ""

let private cleanup (text: string) : string =
    let text = numbering.Replace((withoutEmoji (Html.plain text)).Trim(), "")
    (Html.squeeze (dots.Replace(text, " "))).Trim(' ', ',', '.', ';')

let private czechNumber (text: string) =
    match Decimal.TryParse(text.Replace(" ", "").Replace(',', '.'), CultureInfo.InvariantCulture) with
    | true, value -> Some value
    | _ -> None

let private date = Regex(@"(\d{1,2})\.\s*(\d{1,2})\.\s*(\d{4})")

let private dates (text: string) =
    date.Matches(text)
    |> Seq.map (fun m -> DateOnly(int m.Groups[3].Value, int m.Groups[2].Value, int m.Groups[1].Value))
    |> Seq.toList

let private menickaDays = Regex("<div class='menicka'>(.*?)</ul>", options)
let private menickaTitle = Regex("<div class='nadpis'>(.*?)</div>", options)

let private menickaItems =
    Regex("<li class='(?:polevka|jidlo)'>(.*?)</li>", options)

let private menickaName = Regex("<div class='polozka'>(.*?)</div>", options)
let private menickaPrice = Regex("<div class='cena'>(.*?)</div>", options)
let private menickaOrder = Regex("<span class='poradi'>.*?</span>", options)

/// Items listed for the day; a closed day or one without a menu has none.
let menicka (today: DateOnly) (html: string) : Item list =
    menickaDays.Matches(html)
    |> Seq.map _.Groups[1].Value
    |> Seq.filter (fun day -> dates (menickaTitle.Match(day).Groups[1].Value) = [ today ])
    |> Seq.truncate 1
    |> Seq.collect (fun day -> menickaItems.Matches day)
    |> Seq.map (fun item ->
        let item = item.Groups[1].Value

        {
            Text = cleanup (menickaOrder.Replace(menickaName.Match(item).Groups[1].Value, ""))
            Price = czechNumber ((Html.plain (menickaPrice.Match(item).Groups[1].Value)).Replace("Kč", ""))
        })
    |> Seq.filter (fun item -> item.Text <> "")
    |> Seq.toList

let lepsiMenu (id: string) (json: string) : Item list =
    use document = JsonDocument.Parse json

    match document.RootElement.GetProperty("menus").TryGetProperty(id) with
    | true, menu when menu.ValueKind = JsonValueKind.Array ->
        menu.EnumerateArray()
        |> Seq.map (fun item ->
            {
                Text = cleanup (item.GetProperty("name").GetString())
                Price =
                    match item.TryGetProperty("price") with
                    | true, price when price.ValueKind = JsonValueKind.Number -> Some(price.GetDecimal())
                    | _ -> None
            })
        |> Seq.toList
    | _ -> []

let wolt (category: string) (json: string) : Item list =
    use document = JsonDocument.Parse json
    let root = document.RootElement

    let ids =
        root.GetProperty("categories").EnumerateArray()
        |> Seq.filter (fun c -> c.GetProperty("slug").GetString().Contains(category))
        |> Seq.collect (fun c -> c.GetProperty("item_ids").EnumerateArray() |> Seq.map _.GetString())
        |> Set.ofSeq

    root.GetProperty("items").EnumerateArray()
    |> Seq.filter (fun item -> ids.Contains(item.GetProperty("id").GetString()))
    |> Seq.map (fun item ->
        let name = item.GetProperty("name").GetString()
        let description = item.GetProperty("description").GetString()

        {
            Text =
                cleanup (
                    if String.IsNullOrWhiteSpace description then
                        name
                    else
                        $"{name} – {description}"
                )
            Price = Some(item.GetProperty("price").GetDecimal() / 100m)
        })
    |> Seq.toList

let private taoItems =
    Regex(@"class=""[^""]*tydenni-menu-text[^""]*""[^>]*>(.*?)</div>", options)

let private taoPrice = Regex(@"(\d[\d ]*)\s*[Kk][čc]\u030C?\.?")

let private taoWeek =
    Regex(@"\d{1,2}\.\s*\d{1,2}\.\s*\d{4}\s*[-–]\s*\d{1,2}\.\s*\d{1,2}\.\s*\d{4}")

let private weekdays = [ "pondělí"; "úterý"; "středa"; "čtvrtek"; "pátek" ]

/// The weekly menus (numbered) and today's daily one, if the page is for this week.
let tao (today: DateOnly) (html: string) : Item list =
    let current =
        match dates (taoWeek.Match(Html.plain html).Value) with
        | [ first; last ] -> first <= today && today <= last
        | _ -> true

    match List.tryItem (int today.DayOfWeek - 1) weekdays with
    | Some weekday when current ->
        taoItems.Matches(html)
        |> Seq.map (fun m -> Html.plain m.Groups[1].Value)
        |> Seq.filter (fun item ->
            item.Length > 20
            && (Char.IsDigit item[0] || item.ToLowerInvariant().StartsWith weekday))
        |> Seq.map (fun item ->
            match taoPrice.Matches(item) |> Seq.tryLast with
            | Some price ->
                {
                    Text = cleanup (item.Remove(price.Index, price.Length))
                    Price = czechNumber price.Groups[1].Value
                }
            | None -> { Text = cleanup item; Price = None })
        |> Seq.toList
    | _ -> []
