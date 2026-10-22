/// What a member with only @everyone and the lockout role can do in a channel. Discord.Net
/// computes permissions only for real members, so this applies Discord's rules by hand.
module Masaryk.Core.Services.Lockout.Access

open Discord
open Discord.WebSocket

/// Discord applies the base permission, then @everyone's channel overwrite, then the role's.
let private effective (baseline: bool) (everyone: PermValue) (role: PermValue) : bool =
    let apply value current =
        match value with
        | PermValue.Allow -> true
        | PermValue.Deny -> false
        | _ -> current

    baseline |> apply everyone |> apply role

let private overwrite (get: OverwritePermissions -> PermValue) (channel: SocketGuildChannel) (role: SocketRole) =
    channel.GetPermissionOverwrite(role)
    |> Option.ofNullable
    |> Option.map get
    |> Option.defaultValue PermValue.Inherit

let private holds
    (guild: SocketGuild)
    (role: SocketRole)
    (baseline: GuildPermissions -> bool)
    (get: OverwritePermissions -> PermValue)
    (channel: SocketGuildChannel)
    =
    let everyone = guild.EveryoneRole

    everyone.Permissions.Administrator
    || role.Permissions.Administrator
    || effective
        (baseline everyone.Permissions || baseline role.Permissions)
        (overwrite get channel everyone)
        (overwrite get channel role)

let sees (guild: SocketGuild) (role: SocketRole) (channel: SocketGuildChannel) : bool =
    holds guild role _.ViewChannel _.ViewChannel channel

let posts (guild: SocketGuild) (role: SocketRole) (channel: SocketGuildChannel) : bool =
    holds guild role _.SendMessages _.SendMessages channel
