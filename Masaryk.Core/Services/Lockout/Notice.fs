/// The embed in the timeout channel that tells locked-out members where they are.
module Masaryk.Core.Services.Lockout.Notice

open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open Discord
open Discord.WebSocket

let private title = "🌱 Touch grass"

let private text =
    "You are locked out until the time is up. There is nothing you can do."

let private fileName = "grass.jpg"

let private embed =
    EmbedBuilder()
        .WithTitle(title)
        .WithDescription(text)
        .WithColor(Color.Green)
        .WithImageUrl($"attachment://{fileName}")
        .Build()

let private isNotice (botId: uint64) (message: IMessage) =
    message.Author.Id = botId
    && message.Embeds |> Seq.exists (fun e -> e.Title = title)

let private isCurrent (message: IMessage) =
    message.Embeds |> Seq.exists (fun e -> e.Description = text)

/// Posts the notice unless it's among the channel's last 100 messages, replacing an earlier
/// version of it. Returns whether it posted.
let ensure (client: DiscordSocketClient) (channel: SocketTextChannel) : Task<bool> =
    task {
        let pages = channel.GetMessagesAsync(100)

        let! recent = unbox<IAsyncEnumerable<seq<IMessage>>>(box pages) |> _.FlattenAsync()
        let notices = recent |> Seq.filter (isNotice client.CurrentUser.Id) |> List.ofSeq

        if notices |> List.exists isCurrent then
            return false
        else
            for outdated in notices do
                do! outdated.DeleteAsync()

            use image = Assembly.GetExecutingAssembly().GetManifestResourceStream fileName
            let! _ = channel.SendFileAsync(new FileAttachment(image, fileName), embed = embed)
            return true
    }
