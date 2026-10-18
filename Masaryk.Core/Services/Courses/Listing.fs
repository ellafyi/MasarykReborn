/// `/courses list`: the catalog a page at a time, with buttons to turn pages.
module Masaryk.Core.Services.Courses.Listing

open Discord
open Masaryk.Postgres

let pageSize = 20

let private prefix = "courses-list"

/// Button IDs carry the filter and page, so turning pages needs no stored state.
let buttonId (faculty: string option) (page: int) =
    $"""{prefix}:{Option.defaultValue "" faculty}:{page}"""

let parseButton (id: string) : (string option * int) option =
    match id.Split(':') with
    | [| p; faculty; page |] when p = prefix -> Some((if faculty = "" then None else Some faculty), int page)
    | _ -> None

let render
    (faculty: string option)
    (page: int)
    (courses: (Course * uint64 option) list)
    (total: int64)
    : Embed * MessageComponent =
    let pages = max 1 (int ((total + int64 pageSize - 1L) / int64 pageSize))

    let line (course: Course, channel: uint64 option) =
        let code =
            if faculty.IsSome then
                course.Code
            else
                $"{course.Faculty}:{course.Code}"

        let name =
            if course.Name.Length > 100 then
                course.Name[..98] + "…"
            else
                course.Name

        match channel with
        | Some id -> $"**{code}** {name} · <#{id}>"
        | None -> $"**{code}** {name}"

    let embed =
        EmbedBuilder()
            .WithTitle(
                match faculty with
                | Some faculty -> $"Courses at {faculty}"
                | None -> "All courses"
            )
            .WithDescription(
                if courses.IsEmpty then
                    "No courses."
                else
                    courses |> List.map line |> String.concat "\n"
            )
            .WithFooter($"Page {page + 1} of {pages} · {total} courses")
            .Build()

    let buttons =
        ComponentBuilder()
            .WithButton("◀", buttonId faculty (page - 1), ButtonStyle.Secondary, disabled = (page = 0))
            .WithButton("▶", buttonId faculty (page + 1), ButtonStyle.Secondary, disabled = (page + 1 >= pages))
            .Build()

    embed, buttons
