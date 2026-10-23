module Masaryk.Core.Services.Shared.Html

open System.Net
open System.Text.RegularExpressions

let private tags = Regex("<[^>]+>")
let private spaces = Regex(@"\s+")

let squeeze (text: string) = spaces.Replace(text, " ").Trim()

let plain (html: string) =
    squeeze (WebUtility.HtmlDecode(tags.Replace(html, " ")))
