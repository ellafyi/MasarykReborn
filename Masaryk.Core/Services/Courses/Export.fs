/// Read the IS MU prezentátor export: one HTML table, one row per course and term.
module Masaryk.Core.Services.Courses.Export

open System
open System.Text.RegularExpressions
open Masaryk.Core.Services.Shared
open Masaryk.Postgres

let private tag (name: string) =
    Regex($"<{name}>(.*?)</{name}>", RegexOptions.Singleline ||| RegexOptions.IgnoreCase)

let private rows, headers, cells = tag "TR", tag "TH", tag "TD"

let private values (pattern: Regex) (row: string) =
    pattern.Matches(row)
    |> Seq.map (fun m -> Html.plain m.Groups[1].Value)
    |> Seq.toArray

/// Exchange-programme (XX_) and placeholder (ZZ_) codes aren't real courses.
let private skipped (code: string) =
    code.StartsWith("XX_", StringComparison.OrdinalIgnoreCase)
    || code.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase)

let parse (html: string) : CatalogCourse list =
    match rows.Matches(html) |> Seq.map _.Groups[1].Value |> Seq.toList with
    | [] -> failwith "The export has no table rows."
    | header :: body ->
        let names = values headers header

        let column name =
            match Array.tryFindIndex ((=) name) names with
            | Some index -> index
            | None -> failwith $"The export has no '{name}' column; IS may have changed its format."

        let faculty, code, name = column "Fakulta", column "Kód", column "Název"

        body
        |> List.map (fun row ->
            let row = values cells row

            if row.Length <> names.Length then
                failwith $"A row has {row.Length} cells but the header has {names.Length}."

            row)
        |> List.filter (fun row -> not (skipped row[code]))
        |> List.map (fun row ->
            {
                Faculty = row[faculty]
                Code = row[code]
                Name = row[name]
            })
