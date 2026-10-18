/// What a setting is when the configuration doesn't mention it.
module Masaryk.Configuration.Defaults

let private restaurant name icon color url source =
    {
        Name = name
        Icon = icon
        Color = color
        Url = url
        Source = source
    }

let private menicka id name icon color =
    restaurant name icon color $"https://www.menicka.cz/{id}.html" (Menicka id)

let private wolt = "https://wolt.com/en/cze/brno/restaurant/"

/// Lunch places near FI, taken from HungryBot (github.com/roseckyj/HungryBot).
let private restaurants: Restaurant list =
    [
        menicka 4116 "Padagali" "🍛" 0xf15850u
        restaurant
            "Bistro Bastardo"
            "🌮"
            0x5a5045u
            $"{wolt}bistro-bastardo-stefanikova"
            (Wolt("bistro-bastardo-stefanikova", "tydenni"))
        menicka 2752 "U Dřeváka" "🍔" 0x7c1c14u
        menicka 6695 "U Karla" "🍗" 0xffffffu
        restaurant "Táo Viet Nam" "🍜" 0x66ad2du "https://www.taorestaurant.cz/tydenni_menu/nabidka/" Tao
        menicka 9964 "Hostinec pod schody" "🥞" 0xffcc70u
        restaurant "Caruso Pizza & Pasta" "🍝" 0xffffffu "https://carusofood.cz/denni-menu" (LepsiMenu "caruso-pizza")
        restaurant "Marmite Café" "☕" 0x0e0500u "https://marmite.cz/" (LepsiMenu "marmite-cafe")
        restaurant "Fresh Menu" "🌿" 0x79b741u "http://www.fresh-menu.cz/" (LepsiMenu "fresh-menu")
        menicka 9363 "Bistro na Botance" "🌼" 0x1c5c95u
        menicka 5448 "Light of India" "💡" 0xd49257u
        restaurant "Pelmeka" "🥟" 0x01b1e0u "https://wolt.com/cs/cze/brno/restaurant/pelmeka" (Wolt("pelmeka", "denni"))
        restaurant "Bistro Di Napoli" "🍕" 0x009614u "https://www.di-napoli.cz/" Link
        restaurant "Zô Brno" "🍣" 0xfef8dau "https://zobrno.cz/" Link
        restaurant "Ni Hao" "🥢" 0x0dc68bu "https://www.foodora.cz/restaurant/z0gp/ni-hao" Link
    ]

let configuration: Configuration =
    {
        LogChannel = None
        Boards = []
        HomeFaculty = Some "FI"
        Restaurants = restaurants
    }
