/// The configuration as text: a commented file for people, plain JSON for storage.
/// Missing keys get their default; `"<…>"` placeholders mean "not set".
module Masaryk.Configuration.Document

open System
open System.Globalization
open System.Text
open System.Text.Json

let private isPlaceholder (text: string) =
    text.Length >= 2 && text.StartsWith '<' && text.EndsWith '>'

/// Whether these boards could both claim one message, given every channel that is a target.
let private overlap (targets: Set<uint64>) (left: Board) (right: Board) =
    let sources (board: Board) = Set.difference board.Channels targets

    match left.Mode, right.Mode with
    | SourceMode.Whitelist, SourceMode.Whitelist -> not (Set.intersect (sources left) (sources right)).IsEmpty
    | SourceMode.Whitelist, SourceMode.Blacklist -> not (Set.difference (sources left) right.Channels).IsEmpty
    | SourceMode.Blacklist, SourceMode.Whitelist -> not (Set.difference (sources right) left.Channels).IsEmpty
    | SourceMode.Blacklist, SourceMode.Blacklist -> true

/// Problems with the boards as a whole; each message may belong to at most one board.
let boardProblems (boards: Board list) : string list =
    let duplicates (key: Board -> 'K) =
        boards |> List.countBy key |> List.exists (fun (_, count) -> count > 1)

    let targets = boards |> List.map _.Target |> Set.ofList

    [
        if duplicates _.Name then
            "starboard.boards: board names must be unique"
        if duplicates _.Target then
            "starboard.boards: each board needs its own target channel"
        for index, left in List.indexed boards do
            for right in List.skip (index + 1) boards do
                if overlap targets left right then
                    $"starboard.boards: '{left.Name}' and '{right.Name}' could both claim the same message"
    ]

/// The version the file was exported from, if it says, and the configuration it describes.
let read (json: string) : Result<int64 option * Configuration, string list> =
    let errors = ResizeArray<string>()

    let fail path message : 'T option =
        errors.Add $"{path}: {message}"
        None

    let at path name =
        if path = "" then name else $"{path}.{name}"

    let fields path (known: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail path "must be an object"
        else
            let properties = element.EnumerateObject() |> Seq.toList

            for property in properties do
                if not (List.contains property.Name known) then
                    fail (at path property.Name) "isn't a setting" |> ignore

            Some(properties |> List.map (fun p -> p.Name, p.Value) |> Map.ofList)

    let isUnset (element: JsonElement) =
        element.ValueKind = JsonValueKind.Null
        || element.ValueKind = JsonValueKind.String && isPlaceholder (element.GetString())

    /// A value that must be filled in.
    let need path (values: Map<string, JsonElement>) name (convert: string -> JsonElement -> 'T option) =
        let path = at path name

        match Map.tryFind name values with
        | None -> fail path "is missing"
        | Some value when value.ValueKind = JsonValueKind.String && isPlaceholder (value.GetString()) ->
            fail path "is still a placeholder"
        | Some value when value.ValueKind = JsonValueKind.Null -> fail path "is missing"
        | Some value -> convert path value

    /// A value that may be left out (its default applies) or unset with a placeholder or null.
    let optional path (values: Map<string, JsonElement>) name fallback (convert: string -> JsonElement -> 'T option) =
        match Map.tryFind name values with
        | None -> Some fallback
        | Some value when isUnset value -> Some None
        | Some value -> convert (at path name) value |> Option.map Some

    let text path (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then
            Some(element.GetString())
        else
            fail path "must be text in quotes"

    let discordId path (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String ->
            match UInt64.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, id when id > 0UL -> Some id
            | _ -> fail path "must be a Discord ID"
        | JsonValueKind.Number -> fail path "must be in quotes; Discord IDs are too long for plain numbers"
        | _ -> fail path "must be a Discord ID in quotes"

    let positive path (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Number ->
            match element.TryGetInt32() with
            | true, value when value > 0 -> Some value
            | _ -> fail path "must be a whole number above 0"
        | _ -> fail path "must be a number"

    let flag path (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | _ -> fail path "must be true or false"

    let color path (element: JsonElement) =
        text path element
        |> Option.bind (fun value ->
            match
                value.StartsWith '#' && value.Length = 7,
                UInt32.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            with
            | true, (true, color) -> Some color
            | _ -> fail path "must be a color like \"#f15850\"")

    let list path (item: string -> JsonElement -> 'T option) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then
            fail path "must be a list in [ ]"
        else
            let items =
                element.EnumerateArray()
                |> Seq.mapi (fun index value -> item $"{path}[{index}]" value)
                |> Seq.toList

            if List.forall Option.isSome items then
                Some(List.choose id items)
            else
                None

    let board path element =
        fields path [ "name"; "target"; "mode"; "channels"; "threshold"; "active" ] element
        |> Option.bind (fun values ->
            let name = need path values "name" text
            let target = need path values "target" discordId

            let mode =
                need path values "mode" (fun path value ->
                    match text path value with
                    | Some "whitelist" -> Some SourceMode.Whitelist
                    | Some "blacklist" -> Some SourceMode.Blacklist
                    | Some _ -> fail path "must be \"whitelist\" or \"blacklist\""
                    | None -> None)

            let channels =
                optional path values "channels" (Some []) (fun path value -> list path discordId value)

            let threshold = need path values "threshold" positive
            let active = optional path values "active" (Some true) flag

            match name, target, mode, channels, threshold, active with
            | Some name, Some target, Some mode, Some channels, Some threshold, Some active ->
                Some
                    {
                        Name = name
                        Mode = mode
                        Channels = channels |> Option.defaultValue [] |> Set.ofList
                        Target = target
                        Threshold = threshold
                        IsActive = active |> Option.defaultValue true
                    }
            | _ -> None)

    let source path element =
        fields path [ "type"; "id"; "category" ] element
        |> Option.bind (fun values ->
            match need path values "type" text with
            | Some "menicka" ->
                need path values "id" (fun path value ->
                    text path value
                    |> Option.bind (fun id ->
                        match Int32.TryParse id with
                        | true, id -> Some(Menicka id)
                        | _ -> fail path "must be the number from the menicka.cz address"))
            | Some "lepsimenu" -> need path values "id" text |> Option.map LepsiMenu
            | Some "wolt" ->
                match need path values "id" text, need path values "category" text with
                | Some slug, Some category -> Some(Wolt(slug, category))
                | _ -> None
            | Some "tao" -> Some Tao
            | Some "link" -> Some Link
            | Some _ -> fail (at path "type") "must be menicka, lepsimenu, wolt, tao or link"
            | None -> None)

    let restaurant path element =
        fields path [ "name"; "icon"; "color"; "url"; "source" ] element
        |> Option.bind (fun values ->
            match
                need path values "name" text,
                need path values "icon" text,
                need path values "color" color,
                need path values "url" text,
                need path values "source" source
            with
            | Some name, Some icon, Some color, Some url, Some source ->
                Some
                    {
                        Name = name
                        Icon = icon
                        Color = color
                        Url = url
                        Source = source
                    }
            | _ -> None)

    let section values name (known: string list) =
        match Map.tryFind name values with
        | None -> Some Map.empty
        | Some value -> fields name known value

    let parsed =
        try
            use document =
                JsonDocument.Parse(
                    json,
                    JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)
                )

            fields "" [ "version"; "logChannel"; "starboard"; "courses"; "menu" ] document.RootElement
            |> Option.bind (fun root ->
                let version =
                    match Map.tryFind "version" root with
                    | Some value when value.ValueKind = JsonValueKind.Number ->
                        match value.TryGetInt64() with
                        | true, version -> Some(Some version)
                        | _ -> fail "version" "must be a whole number"
                    | Some _ -> fail "version" "must be a whole number"
                    | None -> Some None

                let logChannel =
                    optional "" root "logChannel" Defaults.configuration.LogChannel discordId

                let starboard = section root "starboard" [ "boards" ]
                let courses = section root "courses" [ "homeFaculty" ]
                let menu = section root "menu" [ "restaurants" ]

                let boards =
                    starboard
                    |> Option.bind (fun values ->
                        optional "starboard" values "boards" (Some Defaults.configuration.Boards) (fun path value ->
                            list path board value))
                    |> Option.map (Option.defaultValue [])

                let homeFaculty =
                    courses
                    |> Option.bind (fun values ->
                        optional "courses" values "homeFaculty" Defaults.configuration.HomeFaculty text)

                let restaurants =
                    menu
                    |> Option.bind (fun values ->
                        optional
                            "menu"
                            values
                            "restaurants"
                            (Some Defaults.configuration.Restaurants)
                            (fun path value -> list path restaurant value))
                    |> Option.map (Option.defaultValue [])

                match version, logChannel, boards, homeFaculty, restaurants with
                | Some version, Some logChannel, Some boards, Some homeFaculty, Some restaurants ->
                    Some(
                        version,
                        {
                            LogChannel = logChannel
                            Boards = boards
                            HomeFaculty = homeFaculty
                            Restaurants = restaurants
                        }
                    )
                | _ -> None)
        with :? JsonException as error ->
            fail $"line {error.LineNumber.GetValueOrDefault() + 1L}" "isn't valid JSON"

    match parsed with
    | Some(version, configuration) when errors.Count = 0 ->
        match boardProblems configuration.Boards with
        | [] -> Ok(version, configuration)
        | problems -> Error problems
    | _ -> Error(List.ofSeq errors)

/// A JSON string that keeps emoji and accents readable; only what JSON requires is escaped.
let private quote (text: string) =
    let escaped = StringBuilder("\"")

    for c in text do
        match c with
        | '"' -> escaped.Append "\\\"" |> ignore
        | '\\' -> escaped.Append "\\\\" |> ignore
        | c when c < ' ' -> escaped.Append $"\\u{int c:x4}" |> ignore
        | c -> escaped.Append c |> ignore

    escaped.Append('"').ToString()

let private sourceJson (source: MenuSource) =
    match source with
    | Menicka id -> $"""{{ "type": "menicka", "id": "{id}" }}"""
    | LepsiMenu id -> $"""{{ "type": "lepsimenu", "id": {quote id} }}"""
    | Wolt(slug, category) -> $"""{{ "type": "wolt", "id": {quote slug}, "category": {quote category} }}"""
    | Tao -> """{ "type": "tao" }"""
    | Link -> """{ "type": "link" }"""

let private boardLines (board: Board) =
    let channels =
        board.Channels |> Seq.map (fun id -> $"\"{id}\"") |> String.concat ", "

    let mode =
        match board.Mode with
        | SourceMode.Whitelist -> "whitelist"
        | SourceMode.Blacklist -> "blacklist"

    let active = if board.IsActive then "true" else "false"

    [
        "{"
        $"  \"name\": {quote board.Name},"
        $"  \"target\": \"{board.Target}\","
        $"  \"mode\": \"{mode}\","
        $"  \"channels\": [{channels}],"
        $"  \"threshold\": {board.Threshold},"
        $"  \"active\": {active}"
        "}"
    ]

let private restaurantLine (restaurant: Restaurant) =
    $"{{ \"name\": {quote restaurant.Name}, \"icon\": {quote restaurant.Icon}, \"color\": \"#{restaurant.Color:x6}\", \"url\": {quote restaurant.Url}, \"source\": {sourceJson restaurant.Source} }}"

/// Lines of a JSON list, each item's lines indented, separated by commas.
let private items (indent: string) (lines: string list list) =
    lines
    |> List.mapi (fun index item ->
        let last = index = lines.Length - 1

        item
        |> List.mapi (fun line text ->
            let comma = if not last && line = item.Length - 1 then "," else ""
            indent + text + comma))
    |> List.concat

let private render (version: int64 option) (comments: bool) (configuration: Configuration) =
    let note (indent: string) (lines: string list) =
        if comments then
            lines |> List.map (fun line -> $"{indent}// {line}")
        else
            []

    let gap = if comments then [ "" ] else []

    let logChannel =
        configuration.LogChannel
        |> Option.map (fun id -> $"\"{id}\"")
        |> Option.defaultValue "\"<channel ID>\""

    let homeFaculty =
        configuration.HomeFaculty
        |> Option.map quote
        |> Option.defaultValue "\"<faculty code>\""

    let title =
        match version with
        | Some version -> $"Masaryk bot configuration, version {version}."
        | None -> "Masaryk bot configuration."

    [
        yield!
            note
                ""
                [
                    title
                    "Change the values, then import the file with /config import. \"<…>\" means not set yet."
                    "Discord IDs are text: right-click a channel → Copy Channel ID (needs Developer Mode)."
                ]
        "{"
        match version with
        | Some version ->
            yield!
                note
                    "  "
                    [
                        "Leave as is: the import is refused if someone changed the configuration since."
                    ]

            $"  \"version\": {version},"
        | None -> ()
        yield! gap
        yield!
            note
                "  "
                [
                    "Where the bot reports progress and problems; make it visible to admins only."
                    "Optional: without it, progress goes to the private reply of the command."
                ]
        $"  \"logChannel\": {logChannel},"
        yield! gap
        "  \"starboard\": {"
        yield!
            note
                "    "
                [
                    "Messages reaching `threshold` reactions with one emoji are reposted to `target`."
                    "mode \"whitelist\": only messages from `channels`; \"blacklist\": every channel except `channels`."
                    "A message may belong to at most one board. \"active\": false keeps a board without posting."
                ]
        "    \"boards\": ["
        yield! items "      " (configuration.Boards |> List.map boardLines)
        yield!
            note
                "      "
                [
                    "Another board:"
                    "{ \"name\": \"<name>\", \"target\": \"<channel ID>\", \"mode\": \"whitelist\", \"channels\": [\"<channel ID>\"], \"threshold\": 10, \"active\": true }"
                ]
        "    ]"
        "  },"
        yield! gap
        "  \"courses\": {"
        yield! note "    " [ "Courses of this faculty get channel names without a faculty prefix." ]
        $"    \"homeFaculty\": {homeFaculty}"
        "  },"
        yield! gap
        "  \"menu\": {"
        yield!
            note
                "    "
                [
                    "Restaurants for /menu, in this order. source type: \"menicka\" (id: the number in its menicka.cz"
                    "address), \"lepsimenu\" (id), \"wolt\" (id: the venue's address slug, category: text in the"
                    "slug of the menu category to show), \"tao\", or \"link\" (no menu, only the url)."
                ]
        "    \"restaurants\": ["
        yield! items "      " (configuration.Restaurants |> List.map (restaurantLine >> List.singleton))
        yield!
            note
                "      "
                [
                    "Another restaurant:"
                    "{ \"name\": \"<name>\", \"icon\": \"<emoji>\", \"color\": \"#ffffff\", \"url\": \"<address>\", \"source\": { \"type\": \"menicka\", \"id\": \"<number>\" } }"
                ]
        "    ]"
        "  }"
        "}"
    ]
    |> String.concat "\n"

/// The file people edit: every setting, explained, with placeholders for unset ones.
let export (version: int64) (configuration: Configuration) : string =
    render (Some version) true configuration

/// Plain JSON for the database.
let store (configuration: Configuration) : string = render None false configuration
