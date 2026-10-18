module Masaryk.Core.Services.Shared.Html

open System.Net
open System.Text.RegularExpressions

let private tags = Regex("<[^>]+>")
let private spaces = Regex(@"\s+")

/// Runs of whitespace as one space, trimmed.
let squeeze (text: string) = spaces.Replace(text, " ").Trim()

/// The text of an HTML fragment: tags removed, entities decoded, whitespace squeezed.
let plain (html: string) =
    squeeze (WebUtility.HtmlDecode(tags.Replace(html, " ")))
