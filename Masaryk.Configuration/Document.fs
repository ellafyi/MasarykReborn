/// The configuration as text: a commented file for people, plain JSON for storage.
/// Missing keys get their default; `"<…>"` placeholders mean "not set".
module Masaryk.Configuration.Document

open System
open System.Globalization
open System.Text.Json

/// Whether these boards could both claim one message, given every channel that is a target.
let private overlap (targets: Set<uint64>) (left: Board) (right: Board) =
    let sources (board: Board) = Set.difference board.Channels targets

    match left.Mode, right.Mode with
    | SourceMode.Whitelist, SourceMode.Whitelist -> not (Set.intersect (sources left) (sources right)).IsEmpty
    | SourceMode.Whitelist, SourceMode.Blacklist -> not (Set.difference (sources left) right.Channels).IsEmpty
    | SourceMode.Blacklist, SourceMode.Whitelist -> not (Set.difference (sources right) left.Channels).IsEmpty
    | SourceMode.Blacklist, SourceMode.Blacklist -> true

/// Problems with the boards as a whole; each message may belong to at most one board.
let private boardProblems (boards: Board list) : string list =
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

    // The returned value is never used: any error fails the read.
    let fail path message : 'T =
        errors.Add $"{path}: {message}"
        Unchecked.defaultof<'T>

    let at path name =
        if path = "" then name else $"{path}.{name}"

    let isUnset (value: JsonElement) =
        value.ValueKind = JsonValueKind.Null
        || value.ValueKind = JsonValueKind.String
           && value.GetString().StartsWith '<'
           && value.GetString().EndsWith '>'

    /// Report unknown keys, then read the object with `build`.
    let record (known: string list) (build: string * JsonElement -> 'T) path (value: JsonElement) : 'T =
        if value.ValueKind <> JsonValueKind.Object then
            fail path "must be an object"
        else
            for property in value.EnumerateObject() do
                if not (List.contains property.Name known) then
                    fail (at path property.Name) "isn't a setting"

            build (path, value)

    let need (path, value: JsonElement) (name: string) (convert: string -> JsonElement -> 'T) : 'T =
        let path = at path name

        match value.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String && isUnset value -> fail path "is still a placeholder"
        | true, value when value.ValueKind <> JsonValueKind.Null -> convert path value
        | _ -> fail path "is missing"

    /// A missing key gets `fallback`; a placeholder or null means unset.
    let optional
        (path, value: JsonElement)
        (name: string)
        (fallback: 'T option)
        (convert: string -> JsonElement -> 'T)
        =
        match value.TryGetProperty name with
        | true, value when isUnset value -> None
        | true, value -> Some(convert (at path name) value)
        | _ -> fallback

    let parsed (problem: string) (parse: string -> 'T option) path (value: JsonElement) : 'T =
        if value.ValueKind <> JsonValueKind.String then
            fail path "must be text in quotes"
        else
            parse (value.GetString()) |> Option.defaultWith (fun () -> fail path problem)

    let text = parsed "" Some

    let discordId path (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Number then
            fail path "must be in quotes; Discord IDs are too long for plain numbers"
        else
            parsed
                "must be a Discord ID"
                (fun text ->
                    match UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
                    | true, id when id > 0UL -> Some id
                    | _ -> None)
                path
                value

    let positive path (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Number then
            fail path "must be a number"
        else
            match value.TryGetInt32() with
            | true, number when number > 0 -> number
            | _ -> fail path "must be a whole number above 0"

    let flag path (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> fail path "must be true or false"

    let color =
        parsed "must be a color like \"#f15850\"" (fun text ->
            match UInt32.TryParse(text.TrimStart '#', NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
            | true, color when text.Length = 7 && text.StartsWith '#' -> Some color
            | _ -> None)

    let list (item: string -> JsonElement -> 'T) path (value: JsonElement) : 'T list =
        if value.ValueKind <> JsonValueKind.Array then
            fail path "must be a list in [ ]"
            []
        else
            [
                for index, element in Seq.indexed (value.EnumerateArray()) -> item $"{path}[{index}]" element
            ]

    let mode =
        parsed "must be \"whitelist\" or \"blacklist\"" (function
            | "whitelist" -> Some SourceMode.Whitelist
            | "blacklist" -> Some SourceMode.Blacklist
            | _ -> None)

    let board =
        record [ "name"; "target"; "mode"; "channels"; "threshold"; "active" ] (fun o ->
            {
                Name = need o "name" text
                Target = need o "target" discordId
                Mode = need o "mode" mode
                Channels =
                    optional o "channels" None (list discordId)
                    |> Option.defaultValue []
                    |> Set.ofList
                Threshold = need o "threshold" positive
                IsActive = optional o "active" None flag |> Option.defaultValue true
            })

    let menickaId =
        parsed "must be the number from the menicka.cz address" (fun text ->
            match Int32.TryParse text with
            | true, id -> Some id
            | _ -> None)

    let source =
        record [ "type"; "id"; "category" ] (fun o ->
            match need o "type" text with
            | "menicka" -> Menicka(need o "id" menickaId)
            | "lepsimenu" -> LepsiMenu(need o "id" text)
            | "wolt" -> Wolt(need o "id" text, need o "category" text)
            | "tao" -> Tao
            | "link"
            | null -> Link // a missing type is already reported
            | _ -> fail (at (fst o) "type") "must be menicka, lepsimenu, wolt, tao or link")

    let restaurant =
        record [ "name"; "icon"; "color"; "url"; "source" ] (fun o ->
            {
                Name = need o "name" text
                Icon = need o "icon" text
                Color = need o "color" color
                Url = need o "url" text
                Source = need o "source" source
            })

    /// A section object holding a single setting.
    let section (path, value: JsonElement) (name: string) key (fallback: 'T option) convert =
        match value.TryGetProperty name with
        | true, value -> record [ key ] (fun s -> optional s key fallback convert) (at path name) value
        | _ -> fallback

    try
        use document =
            JsonDocument.Parse(
                json,
                JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)
            )

        let result =
            record
                [ "version"; "logChannel"; "starboard"; "courses"; "menu" ]
                (fun o ->
                    let version =
                        match (snd o).TryGetProperty "version" with
                        | true, value when value.ValueKind = JsonValueKind.Number && fst (value.TryGetInt64()) ->
                            Some(value.GetInt64())
                        | true, _ -> fail "version" "must be a whole number"
                        | _ -> None

                    version,
                    {
                        LogChannel = optional o "logChannel" Defaults.configuration.LogChannel discordId
                        Boards =
                            section o "starboard" "boards" (Some Defaults.configuration.Boards) (list board)
                            |> Option.defaultValue []
                        HomeFaculty = section o "courses" "homeFaculty" Defaults.configuration.HomeFaculty text
                        Restaurants =
                            section o "menu" "restaurants" (Some Defaults.configuration.Restaurants) (list restaurant)
                            |> Option.defaultValue []
                    })
                ""
                document.RootElement

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            match boardProblems (snd result).Boards with
            | [] -> Ok result
            | problems -> Error problems
    with :? JsonException as error ->
        Error [ $"line {error.LineNumber.GetValueOrDefault() + 1L}: isn't valid JSON" ]

/// A JSON string that keeps emoji and accents readable; System.Text.Json would escape emoji.
let private quote (text: string) =
    let escape c =
        match c with
        | '"' -> "\\\""
        | '\\' -> "\\\\"
        | c when c < ' ' -> $"\\u{int c:x4}"
        | c -> string c

    "\"" + String.collect escape text + "\""

let private sourceJson (source: MenuSource) =
    match source with
    | Menicka id -> $"""{{ "type": "menicka", "id": "{id}" }}"""
    | LepsiMenu id -> $"""{{ "type": "lepsimenu", "id": {quote id} }}"""
    | Wolt(slug, category) -> $"""{{ "type": "wolt", "id": {quote slug}, "category": {quote category} }}"""
    | Tao -> """{ "type": "tao" }"""
    | Link -> """{ "type": "link" }"""

let private boardJson (board: Board) =
    let channels =
        board.Channels |> Seq.map (fun id -> $"\"{id}\"") |> String.concat ", "

    let mode =
        if board.Mode = SourceMode.Whitelist then
            "whitelist"
        else
            "blacklist"

    let active = if board.IsActive then "true" else "false"

    $$"""
      {
        "name": {{quote board.Name}},
        "target": "{{board.Target}}",
        "mode": "{{mode}}",
        "channels": [{{channels}}],
        "threshold": {{board.Threshold}},
        "active": {{active}}
      }"""

let private restaurantJson (restaurant: Restaurant) =
    $$"""
      { "name": {{quote restaurant.Name}}, "icon": {{quote restaurant.Icon}}, "color": "#{{restaurant.Color.ToString "x6"}}", "url": {{quote restaurant.Url}}, "source": {{sourceJson restaurant.Source}} }"""

/// The file people edit: every setting, explained, with placeholders for unset ones.
let export (version: int64) (configuration: Configuration) : string =
    let logChannel =
        configuration.LogChannel
        |> Option.map (fun id -> $"\"{id}\"")
        |> Option.defaultValue "\"<channel ID>\""

    let homeFaculty =
        configuration.HomeFaculty
        |> Option.map quote
        |> Option.defaultValue "\"<faculty code>\""

    let boards = configuration.Boards |> List.map boardJson |> String.concat ","

    let restaurants =
        configuration.Restaurants |> List.map restaurantJson |> String.concat ","

    let file =
        $$"""// Masaryk bot configuration, version {{version}}.
// Change the values, then import the file with /config import. "<…>" means not set yet.
// Discord IDs are text: right-click a channel → Copy Channel ID (needs Developer Mode).
{
  // Leave as is: the import is refused if someone changed the configuration since.
  "version": {{version}},

  // Where the bot reports progress and problems; make it visible to admins only.
  // Optional: without it, progress goes to the private reply of the command.
  "logChannel": {{logChannel}},

  "starboard": {
    // Messages reaching `threshold` reactions with one emoji are reposted to `target`.
    // mode "whitelist": only messages from `channels`; "blacklist": every channel except `channels`.
    // A message may belong to at most one board. "active": false keeps a board without posting.
    "boards": [{{boards}}
      // Another board:
      // { "name": "<name>", "target": "<channel ID>", "mode": "whitelist", "channels": ["<channel ID>"], "threshold": 10, "active": true }
    ]
  },

  "courses": {
    // Courses of this faculty get channel names without a faculty prefix.
    "homeFaculty": {{homeFaculty}}
  },

  "menu": {
    // Restaurants for /menu, in this order. source type: "menicka" (id: the number in its menicka.cz
    // address), "lepsimenu" (id), "wolt" (id: the venue's address slug, category: text in the
    // slug of the menu category to show), "tao", or "link" (no menu, only the url).
    "restaurants": [{{restaurants}}
      // Another restaurant:
      // { "name": "<name>", "icon": "<emoji>", "color": "#ffffff", "url": "<address>", "source": { "type": "menicka", "id": "<number>" } }
    ]
  }
}"""

    file.ReplaceLineEndings "\n"

/// Plain JSON for the database: the export without comments, blank lines and version.
let store (configuration: Configuration) : string =
    (export 0L configuration).Split('\n')
    |> Array.filter (fun line ->
        let line = line.Trim()
        line <> "" && not (line.StartsWith "//") && not (line.StartsWith "\"version\""))
    |> String.concat "\n"
