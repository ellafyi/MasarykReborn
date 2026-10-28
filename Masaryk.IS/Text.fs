module internal Masaryk.IS.Text

open System.Net
open System.Text.RegularExpressions

let private tags = Regex("<[^>]+>")
let private spaces = Regex(@"\s+")

/// IS markup as one line of plain text.
let plain (html: string) =
    spaces.Replace(WebUtility.HtmlDecode(tags.Replace(html, " ")), " ").Trim()
