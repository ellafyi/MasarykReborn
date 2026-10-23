/// Building slash command options and reading what the user filled in.
module Masaryk.Core.Options

open Discord
open Discord.WebSocket

let option (name: string) (description: string) (kind: ApplicationCommandOptionType) =
    SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(kind)

let subcommand (name: string) (description: string) (options: SlashCommandOptionBuilder list) =
    (option name description ApplicationCommandOptionType.SubCommand).AddOptions(List.toArray options)

let textChannel (name: string) (description: string) =
    (option name description ApplicationCommandOptionType.Channel).AddChannelType(ChannelType.Text).WithRequired(true)

/// A channel option for counting statistics; threads and forum posts count toward their parent.
let channelFilter (description: string) =
    let channel = option "channel" description ApplicationCommandOptionType.Channel

    for kind in
        [
            ChannelType.Text
            ChannelType.News
            ChannelType.Forum
            ChannelType.Voice
            ChannelType.Stage
        ] do
        channel.AddChannelType kind |> ignore

    channel

/// The chosen subcommand ("" if the command has none) and the options the user filled in.
let input (interaction: SocketSlashCommand) =
    let top = List.ofSeq interaction.Data.Options

    let name, options =
        match top with
        | [ only ] when only.Type = ApplicationCommandOptionType.SubCommand -> only.Name, List.ofSeq only.Options
        | _ -> "", top

    let value key =
        options |> List.tryFind (fun o -> o.Name = key) |> Option.map _.Value

    name, value
