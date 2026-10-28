/// Runs Prezentátor templates in IS as a neosobní účet.
module Masaryk.IS.Prezentator

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks
open System.Xml.Linq

let private endpoint = Uri "https://is.muni.cz/auth/prezentator/"

type Credentials = { Uco: string; Password: string }

/// From `IS_UCO` and `IS_PASSWORD`. None if either is unset.
let credentials () : Credentials option =
    match Environment.GetEnvironmentVariable "IS_UCO", Environment.GetEnvironmentVariable "IS_PASSWORD" with
    | uco, password when String.IsNullOrWhiteSpace uco || String.IsNullOrEmpty password -> None
    | uco, password ->
        Some
            {
                Uco = uco.Trim()
                Password = password
            }

/// IS refused the account, so every other request would fail the same way.
exception Refused of reason: string with
    override this.Message = this.reason

/// Prezentátor can be slow, and one full 22-column export took 20 minutes.
let client () : HttpClient =
    new HttpClient(Timeout = TimeSpan.FromMinutes 30.0)

let private element (name: string) (content: XObject list) =
    XElement(XName.Get name, List.toArray content)

let private attribute (name: string) (value: string) = XAttribute(XName.Get name, value)

/// Rows of faculty, code and name, the columns Export.parse reads.
let courseTemplate (faculty: Faculty) (term: string) : string =
    let cell tag = element "TD" [ element tag [] ]
    let header (text: string) = element "TH" [ XText text ]

    let template =
        element
            "TELO"
            [
                element "FAKULTA" [ XText(string faculty.Number) ]
                element "OBDOBI" [ XText term ]
                element
                    "TFORMA"
                    [
                        element "TR" [ cell "F_PREDM_FAKULTA"; cell "F_PREDM_KOD_BEZ_FAK"; cell "F_PREDM_NAZEV" ]
                    ]
                element
                    "TABLE"
                    [
                        element "TR" [ header "Fakulta"; header "Kód"; header "Název" ]
                        // DATA_PREDM lists at most 50 courses unless MAX says otherwise.
                        element "DATA_PREDM" [ attribute "MAX" "100000"; element "PREDM_KOD" [ attribute "KOD" "%" ] ]
                    ]
            ]

    template.ToString(SaveOptions.DisableFormatting)

let private part (name: string) (content: HttpContent) : HttpContent =
    content.Headers.ContentDisposition <- ContentDispositionHeaderValue("form-data", Name = $"\"{name}\"")
    content

/// Mimics curl's form, which IS is known to accept.
let private form (template: string) : HttpContent =
    let boundary = Guid.NewGuid().ToString "N"
    let form = new MultipartFormDataContent(boundary)
    (form.Headers.ContentType.Parameters |> Seq.find (fun p -> p.Name = "boundary")).Value <- boundary
    let file = part "keyfile" (new ByteArrayContent(Encoding.UTF8.GetBytes template))
    file.Headers.ContentDisposition.FileName <- "\"predloha.xml\""
    file.Headers.ContentType <- MediaTypeHeaderValue "application/xml"
    form.Add file

    for name, value in
        [
            "keyfile2", ""
            "soubor", "a"
            "typ", "xml"
            "kodovani", "utf-8"
            "vlozit", "Zpracovat předlohu"
        ] do
        let field = part name (new StringContent(value))
        field.Headers.ContentType <- null
        form.Add field

    form

let private send (http: HttpClient) (credentials: Credentials) (template: string) : Task<HttpResponseMessage> =
    task {
        use request =
            new HttpRequestMessage(HttpMethod.Post, endpoint, Content = form template)

        let token =
            Convert.ToBase64String(Encoding.UTF8.GetBytes $"{credentials.Uco}:{credentials.Password}")

        request.Headers.Authorization <- AuthenticationHeaderValue("Basic", token)
        request.Headers.Add("Origin", "https://is.muni.cz")
        request.Headers.Referrer <- endpoint
        return! http.SendAsync request
    }

let run (http: HttpClient) (credentials: Credentials) (template: string) : Task<string> =
    task {
        let! sent =
            task {
                try
                    return! send http credentials template
                with :? HttpRequestException ->
                    // IS now and then drops a request before answering.
                    return! send http credentials template
            }

        use response = sent
        let! bytes = response.Content.ReadAsByteArrayAsync()
        let body = Encoding.UTF8.GetString(bytes).TrimStart '\uFEFF'

        let start () =
            let text = Text.plain body
            if text.Length > 300 then text[..299] + "…" else text

        let page =
            response.Content.Headers.ContentType
            |> Option.ofObj
            |> Option.exists (fun t -> t.MediaType = "text/html")

        match response.StatusCode with
        | HttpStatusCode.Unauthorized -> return raise (Refused "IS rejected the učo or password (401).")
        | HttpStatusCode.Forbidden ->
            return
                raise (
                    Refused
                        $"IS refused the request (403). Allow this IP address or application for the account in IS. IS said: {start ()}"
                )
        | status when not response.IsSuccessStatusCode -> return failwith $"IS answered {int status}: {start ()}"
        // Prezentátor reports template errors, such as an unknown term, as a page instead of an export.
        | _ when page -> return failwith $"IS answered with a page instead of the export: {start ()}"
        | _ -> return body
    }

let courses (http: HttpClient) (credentials: Credentials) (faculty: Faculty) (term: string) : Task<CatalogCourse list> =
    task {
        let! export = run http credentials (courseTemplate faculty term)
        return Export.parse export
    }
