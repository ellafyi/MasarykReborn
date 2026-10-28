/// IS term names, such as `podzim 2026`.
module Masaryk.IS.Terms

open System

/// The terms of the academic year a date falls in, oldest first. An academic year starts in August.
let ofAcademicYear (date: DateOnly) : string list =
    let start = if date.Month >= 8 then date.Year else date.Year - 1
    [ $"podzim {start}"; $"jaro {start + 1}" ]
