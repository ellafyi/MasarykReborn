/// Read the IS MU prezentátor export: one HTML table, one row per course and term.
module Masaryk.Core.Services.Courses.Export

open System
open System.Net
open System.Text.RegularExpressions
open Masaryk.Postgres

let private rows =
    Regex("<TR>(.*?)</TR>", RegexOptions.Singleline ||| RegexOptions.IgnoreCase)

let private headers =
    Regex("<TH>(.*?)</TH>", RegexOptions.Singleline ||| RegexOptions.IgnoreCase)

let private cells =
    Regex("<TD>(.*?)</TD>", RegexOptions.Singleline ||| RegexOptions.IgnoreCase)

let private spaces = Regex(@"\s+")

let private values (pattern: Regex) (row: string) =
    pattern.Matches(row)
    |> Seq.map (fun m -> m.Groups[1].Value.Trim())
    |> Seq.toArray

let private text (html: string) =
    spaces.Replace(WebUtility.HtmlDecode html, " ").Trim()

let term (text: string) : string =
    match text.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
    | [| "podzim"; year |] -> $"fall {year}"
    | [| "jaro"; year |] -> $"spring {year}"
    | _ -> failwith $"Unknown term '{text}'."

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

        let faculty, code, period, name, goals =
            column "Fakulta", column "Kód", column "Období", column "Název", column "Cíle"

        body
        |> List.choose (fun row ->
            let row = values cells row

            if row.Length <> names.Length then
                failwith $"A row has {row.Length} cells but the header has {names.Length}."

            let code = text row[code]

            if skipped code then
                None
            else
                Some
                    {
                        Faculty = text row[faculty]
                        Code = code
                        Term = term (text row[period])
                        Description =
                            if row[goals] = "" then
                                None
                            else
                                Some(row[goals].ReplaceLineEndings "\n")
                        Name = text row[name]
                    })
