/// Fetch every restaurant's menu at once and keep the result for a while.
module Masaryk.Core.Services.Menu.Fetching

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Masaryk.Core.Services.Menu.Restaurants

type Menu =
    /// Today's items; none means no menu today.
    | Items of Parsing.Item list
    | Unavailable
    | LinkOnly

type Menus =
    {
        Day: DateOnly
        Fetched: DateTime
        Menus: (Restaurant * Menu) list
    }

let private http =
    let client = new HttpClient(Timeout = TimeSpan.FromSeconds 10.0)
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MasarykBOT)")
    client

let today () : DateOnly =
    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Europe/Prague"))

let private fetch (day: DateOnly) (lepsiMenu: Lazy<Task<string>>) (restaurant: Restaurant) : Task<Menu> =
    task {
        try
            match restaurant.Source with
            | Menicka _ ->
                let! html = http.GetStringAsync restaurant.Url
                return Items(Parsing.menicka day html)
            | LepsiMenu id ->
                let! json = lepsiMenu.Value
                return Items(Parsing.lepsiMenu id json)
            | Wolt(slug, category) ->
                let! json =
                    http.GetStringAsync
                        $"https://consumer-api.wolt.com/consumer-api/consumer-assortment/v1/venues/slug/{slug}/assortment"

                return Items(Parsing.wolt category json)
            | Tao ->
                let! html = http.GetStringAsync restaurant.Url
                return Items(Parsing.tao day html)
            | Link -> return LinkOnly
        with error ->
            printfn $"Menu: {restaurant.Name} failed: {error.Message}"
            return Unavailable
    }

let private lifetime = TimeSpan.FromMinutes 15.0
let private gate = new SemaphoreSlim(1)
let mutable private cached: Menus option = None

/// Today's menus, fetched again when older than 15 minutes.
let get () : Task<Menus> =
    task {
        do! gate.WaitAsync()

        try
            let day = today ()

            match cached with
            | Some menus when menus.Day = day && DateTime.UtcNow - menus.Fetched < lifetime -> return menus
            | _ ->
                let lepsiMenu = lazy (http.GetStringAsync "https://www.lepsimenu.cz/api/menus")

                let! menus = Restaurants.all |> List.map (fetch day lepsiMenu) |> Task.WhenAll

                let fresh =
                    {
                        Day = day
                        Fetched = DateTime.UtcNow
                        Menus = List.zip Restaurants.all (List.ofArray menus)
                    }

                cached <- Some fresh
                return fresh
        finally
            gate.Release() |> ignore
    }
