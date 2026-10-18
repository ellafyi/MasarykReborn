module Masaryk.Core.Services.Menu.Feature

open System
open System.Threading.Tasks
open Discord.WebSocket
open Masaryk.Core

let private show (interaction: SocketSlashCommand) : Task =
    task {
        do! interaction.DeferAsync()
        let! menus = Fetching.get ()
        let content, embeds, buttons = Rendering.render menus 0
        let! _ = interaction.FollowupAsync(content, embeds = embeds, components = buttons)
        ()
    }

let private turnPage (interaction: SocketMessageComponent) : Task =
    task {
        match Rendering.parseButton interaction.Data.CustomId with
        | Some page ->
            do! interaction.DeferAsync()
            let! menus = Fetching.get ()
            let content, embeds, buttons = Rendering.render menus page

            let! _ =
                interaction.ModifyOriginalResponseAsync(fun message ->
                    message.Content <- content
                    message.Embeds <- embeds
                    message.Components <- buttons)

            ()
        | None -> ()
    }

let start: Feature =
    fun context ->
        task {
            context.Client.add_ButtonExecuted (Func<SocketMessageComponent, Task>(turnPage))

            return
                [
                    {
                        Name = "menu"
                        Description = "Today's lunch menus near FI"
                        Options = []
                        Permission = None
                        Handle = show
                    }
                ]
        }
