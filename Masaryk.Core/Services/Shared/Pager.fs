/// ◀ ▶ buttons whose IDs carry everything needed to show another page.
module Masaryk.Core.Services.Shared.Pager

open Discord

let count (pageSize: int) (total: int64) =
    max 1 (int ((total + int64 pageSize - 1L) / int64 pageSize))

let buttons (id: int -> string) (page: int) (pages: int) : MessageComponent =
    ComponentBuilder()
        .WithButton("◀", id (page - 1), ButtonStyle.Secondary, disabled = (page = 0))
        .WithButton("▶", id (page + 1), ButtonStyle.Secondary, disabled = (page + 1 >= pages))
        .Build()
