/// ◀ ▶ buttons whose IDs (`prefix:state:page`) carry everything needed to show another page.
module Masaryk.Core.Services.Shared.Pager

open Discord

let count (pageSize: int) (total: int64) =
    max 1 (int ((total + int64 pageSize - 1L) / int64 pageSize))

let buttons (prefix: string) (state: string) (page: int) (pages: int) : MessageComponent =
    let id page = $"{prefix}:{state}:{page}"

    ComponentBuilder()
        .WithButton("◀", id (page - 1), ButtonStyle.Secondary, disabled = (page = 0))
        .WithButton("▶", id (page + 1), ButtonStyle.Secondary, disabled = (page + 1 >= pages))
        .Build()

/// The state and page of a button made by `buttons` with this prefix.
let parse (prefix: string) (id: string) : (string * int) option =
    match id.Split(':') with
    | [| p; state; page |] when p = prefix -> Some(state, int page)
    | _ -> None
