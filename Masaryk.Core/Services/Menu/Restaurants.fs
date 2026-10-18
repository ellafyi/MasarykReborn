/// Lunch places near FI, taken from HungryBot (github.com/roseckyj/HungryBot).
module Masaryk.Core.Services.Menu.Restaurants

type Source =
    /// A restaurant page on menicka.cz.
    | Menicka of id: int
    /// A restaurant in the lepsimenu.cz API.
    | LepsiMenu of id: string
    /// A Wolt venue; only menu categories whose slug contains the given text count.
    | Wolt of slug: string * category: string
    /// Táo's own weekly menu page.
    | Tao
    /// No menu online, just a link.
    | Link

type Restaurant =
    {
        Name: string
        Icon: string
        Color: uint32
        Url: string
        Source: Source
    }

let private menicka id name icon color =
    {
        Name = name
        Icon = icon
        Color = color
        Url = $"https://www.menicka.cz/{id}.html"
        Source = Menicka id
    }

let all: Restaurant list =
    [
        menicka 4116 "Padagali" "🍛" 0xf15850u
        {
            Name = "Bistro Bastardo"
            Icon = "🌮"
            Color = 0x5a5045u
            Url = "https://wolt.com/en/cze/brno/restaurant/bistro-bastardo-stefanikova"
            Source = Wolt("bistro-bastardo-stefanikova", "tydenni")
        }
        menicka 2752 "U Dřeváka" "🍔" 0x7c1c14u
        menicka 6695 "U Karla" "🍗" 0xffffffu
        {
            Name = "Táo Viet Nam"
            Icon = "🍜"
            Color = 0x66ad2du
            Url = "https://www.taorestaurant.cz/tydenni_menu/nabidka/"
            Source = Tao
        }
        menicka 9964 "Hostinec pod schody" "🥞" 0xffcc70u
        {
            Name = "Caruso Pizza & Pasta"
            Icon = "🍝"
            Color = 0xffffffu
            Url = "https://carusofood.cz/denni-menu"
            Source = LepsiMenu "caruso-pizza"
        }
        {
            Name = "Marmite Café"
            Icon = "☕"
            Color = 0x0e0500u
            Url = "https://marmite.cz/"
            Source = LepsiMenu "marmite-cafe"
        }
        {
            Name = "Fresh Menu"
            Icon = "🌿"
            Color = 0x79b741u
            Url = "http://www.fresh-menu.cz/"
            Source = LepsiMenu "fresh-menu"
        }
        menicka 9363 "Bistro na Botance" "🌼" 0x1c5c95u
        menicka 5448 "Light of India" "💡" 0xd49257u
        {
            Name = "Pelmeka"
            Icon = "🥟"
            Color = 0x01b1e0u
            Url = "https://wolt.com/cs/cze/brno/restaurant/pelmeka"
            Source = Wolt("pelmeka", "denni")
        }
        {
            Name = "Bistro Di Napoli"
            Icon = "🍕"
            Color = 0x009614u
            Url = "https://www.di-napoli.cz/"
            Source = Link
        }
        {
            Name = "Zô Brno"
            Icon = "🍣"
            Color = 0xfef8dau
            Url = "https://zobrno.cz/"
            Source = Link
        }
        {
            Name = "Ni Hao"
            Icon = "🥢"
            Color = 0x0dc68bu
            Url = "https://www.foodora.cz/restaurant/z0gp/ni-hao"
            Source = Link
        }
    ]
