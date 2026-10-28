namespace Masaryk.IS

type Faculty =
    {
        /// Prezentátor selects a faculty by this number.
        Number: int
        /// How IS prints the faculty of a course, e.g. `FI`.
        Abbreviation: string
        /// IS addresses use English faculty abbreviations.
        Slug: string option
    }

/// One course of a Prezentátor export.
type CatalogCourse =
    {
        Faculty: string
        Code: string
        Name: string
    }

module Faculties =
    let all: Faculty list =
        [
            {
                Number = 1411
                Abbreviation = "LF"
                Slug = Some "med"
            }
            {
                Number = 1416
                Abbreviation = "FaF"
                Slug = Some "pharm"
            }
            {
                Number = 1421
                Abbreviation = "FF"
                Slug = Some "phil"
            }
            {
                Number = 1422
                Abbreviation = "PrF"
                Slug = Some "law"
            }
            {
                Number = 1423
                Abbreviation = "FSS"
                Slug = Some "fss"
            }
            {
                Number = 1431
                Abbreviation = "PřF"
                Slug = Some "sci"
            }
            {
                Number = 1433
                Abbreviation = "FI"
                Slug = Some "fi"
            }
            {
                Number = 1441
                Abbreviation = "PdF"
                Slug = Some "ped"
            }
            {
                Number = 1451
                Abbreviation = "FSpS"
                Slug = Some "fsps"
            }
            {
                Number = 1456
                Abbreviation = "ESF"
                Slug = Some "econ"
            }
            {
                Number = 1490
                Abbreviation = "CST"
                Slug = None
            }
        ]

    /// The public IS page of a course, if its faculty's address is known.
    let coursePage (faculty: string) (code: string) : string option =
        all
        |> List.tryFind (fun f -> f.Abbreviation = faculty)
        |> Option.bind _.Slug
        |> Option.map (fun slug -> $"https://is.muni.cz/predmet/{slug}/{code}")
