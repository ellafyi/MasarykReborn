/// How course channels are named, placed and recognised.
module Masaryk.Core.Services.Courses.Channels

open System
open Masaryk.Postgres

/// The old bot's naming: look-alike characters keep what Discord would strip,
/// and an en dash inside the name keeps the first hyphen as the end of the code.
let sanitize (name: string) : string =
    let words =
        name.ToLowerInvariant().Replace("-", "–").Split((null: char array), StringSplitOptions.RemoveEmptyEntries)

    (String.Join("-", words))
        .Replace("+", "﹢")
        .Replace(".", "․")
        .Replace(",", "")
        .Replace("#", "＃")
        .Replace("/", "／")
        .Replace("(", "")
        .Replace(")", "")
        .Replace(":", "꞉")
        .Replace("?", "？")

/// Home faculty courses (see the `courses.homeFaculty` setting) have no faculty prefix.
let name (home: string option) (course: Course) : string =
    let full =
        if Some course.Faculty = home then
            sanitize $"{course.Code} {course.Name}"
        else
            sanitize $"{course.Faculty}:{course.Code} {course.Name}"

    if full.Length > 100 then full[..99] else full

/// The faculty and code a channel name starts with, if it has the course channel form.
let code (home: string option) (channel: string) : (string * string) option =
    let start = channel.Split('-')[0]

    match start.Split('꞉') with
    | [| code |] when code <> "" -> home |> Option.map (fun home -> home, code)
    | [| faculty; code |] when faculty <> "" && code <> "" -> Some(faculty, code)
    | _ -> None

/// IS addresses use English faculty abbreviations.
let private faculties =
    dict
        [
            "FI", "fi"
            "FF", "phil"
            "PřF", "sci"
            "LF", "med"
            "PdF", "ped"
            "FSS", "fss"
            "PrF", "law"
            "ESF", "econ"
            "FSpS", "fsps"
            "FaF", "pharm"
        ]

let topic (course: Course) : string =
    match faculties.TryGetValue course.Faculty with
    | true, slug -> $"{course.Name} · https://is.muni.cz/predmet/{slug}/{course.Code}"
    | _ -> course.Name

/// Courses sit in a category named by the code's leading letters, or by faculty outside the home faculty.
let category (home: string option) (course: Course) : string =
    if Some course.Faculty = home then
        let letters = course.Code |> Seq.takeWhile Char.IsLetter |> Seq.toArray |> String

        if letters = "" then
            course.Code
        else
            letters.ToUpperInvariant()
    else
        course.Faculty

/// Discord's limit on channels in a category.
let categorySize = 50

/// The first of `name`, `name 2`, `name 3`… with room for another channel.
let roomIn (name: string) (sizes: Map<string, int>) : string =
    Seq.initInfinite (fun i -> if i = 0 then name else $"{name} {i + 1}")
    |> Seq.find (fun candidate -> Map.tryFind candidate sizes |> Option.defaultValue 0 < categorySize)

/// Whether `category` is `name` or one of its numbered overflow categories.
let isOneOf (name: string) (category: string) : bool =
    category = name
    || category.StartsWith(name + " ")
       && category.Length > name.Length + 1
       && Seq.forall Char.IsDigit (category.Substring(name.Length + 1))

type Matching =
    {
        /// Course and channel pairs to link.
        Links: (Course * uint64) list
        /// Channels named like a course that isn't in the catalog.
        Unknown: string list
        /// Courses more than one channel claims; none of them gets linked.
        Contested: (Course * string list) list
    }

let private key (faculty: string, code: string) =
    faculty.ToLowerInvariant(), code.ToLowerInvariant()

/// Match unlinked channels to unlinked courses by the code their name starts with.
/// Only names that start with letters followed by a digit count as course-like.
let matching
    (home: string option)
    (courses: Course list)
    (linked: Set<int64>)
    (channels: (uint64 * string) list)
    : Matching =
    let byCode = courses |> List.map (fun c -> key (c.Faculty, c.Code), c) |> Map.ofList

    let courseLike (_, code: string) =
        code.Length > 1 && Char.IsLetter code[0] && Seq.exists Char.IsDigit code

    let found =
        channels
        |> List.choose (fun (id, name) ->
            code home name
            |> Option.filter courseLike
            |> Option.map (fun c -> id, name, Map.tryFind (key c) byCode))

    let claims =
        found
        |> List.choose (fun (id, name, course) -> course |> Option.map (fun c -> c, (id, name)))
        |> List.filter (fun (course, _) -> not (linked.Contains course.Id))
        |> List.groupBy (fst >> _.Id)
        |> List.map (fun (_, group) -> fst group.Head, List.map snd group)

    {
        Links =
            claims
            |> List.choose (fun (course, channels) ->
                match channels with
                | [ id, _ ] -> Some(course, id)
                | _ -> None)
        Unknown =
            found
            |> List.filter (fun (_, _, c) -> c.IsNone)
            |> List.map (fun (_, name, _) -> name)
        Contested =
            claims
            |> List.filter (fun (_, channels) -> channels.Length > 1)
            |> List.map (fun (course, channels) -> course, List.map snd channels)
    }
