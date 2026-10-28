/// `/touch-grass-check`: whether the lockout is set up so that it works. Changes nothing.
module Masaryk.Core.Services.Lockout.Check

open Discord
open Discord.WebSocket
open Masaryk.Configuration

let private ok (text: string) = $"✅ {text}"
let private bad (text: string) = $"❌ {text}"
let private warn (text: string) = $"⚠️ {text}"
let private note (text: string) = $"ℹ️ {text}"

let private some (items: string list) =
    String.concat " " (List.truncate 10 items)
    + if items.Length > 10 then
          $" and {items.Length - 10} more"
      else
          ""

let private mention (channel: SocketGuildChannel) = $"<#{channel.Id}>"

let private permissionNames (role: SocketRole) =
    role.Permissions.ToList() |> Seq.map string |> String.concat ", "

let private roleLines (guild: SocketGuild) (role: SocketRole) : string list =
    [
        if role.IsEveryone || role.IsManaged then
            bad $"<@&{role.Id}> is @everyone or a bot or booster role; it can't be given out."
        elif role.Permissions.Administrator then
            bad $"<@&{role.Id}> has Administrator, which shows it every channel."
        elif role.Permissions.RawValue <> 0UL then
            warn $"<@&{role.Id}> has permissions ({permissionNames role}); it needs none."
        else
            ok $"<@&{role.Id}> has no permissions."

        if role.Position < Members.botRank guild then
            ok "The lockout role is below my highest role."
        else
            bad "The lockout role isn't below my highest role, so I can't give it out."
    ]

let private botLines (guild: SocketGuild) : string list =
    let permissions = guild.CurrentUser.GuildPermissions

    [
        if permissions.Administrator || permissions.ManageRoles then
            ok "I can manage roles."
        else
            bad "I lack the Manage Roles permission."
    ]

let private channelLines (guild: SocketGuild) (settings: LockoutSettings) (role: SocketRole) : string list =
    let others =
        Hiding.channels guild |> List.filter (fun c -> c.Id <> settings.ChannelId)

    let leaks = others |> List.filter (Access.sees guild role)

    [
        match guild.GetTextChannel settings.ChannelId with
        | null -> bad $"There's no text channel {settings.ChannelId}."
        | channel ->
            if Access.sees guild role channel then
                ok $"The role can read <#{channel.Id}>."
            else
                bad $"The role can't see <#{channel.Id}>, so locked-out members would see nothing."

            if Access.posts guild role channel then
                bad $"The role can write in <#{channel.Id}>; it should be read-only."
            else
                ok $"<#{channel.Id}> is read-only for the role."

            let mine = guild.CurrentUser.GetPermissions(channel)

            let missing =
                [
                    "View Channel", mine.ViewChannel
                    "Read Message History", mine.ReadMessageHistory
                    "Send Messages", mine.SendMessages
                    "Embed Links", mine.EmbedLinks
                    "Attach Files", mine.AttachFiles
                ]
                |> List.filter (snd >> not)
                |> List.map fst
                |> String.concat ", "

            if missing = "" then
                ok $"I can post the grass notice in <#{channel.Id}>."
            else
                bad $"In <#{channel.Id}> I lack {missing}, which I need to post the grass notice."

        if leaks.IsEmpty then
            ok $"All {others.Length} other channels are hidden from the role."
        else
            bad $"{leaks.Length} other channels are still visible to the role: {some (leaks |> List.map mention)}"

            let stuck =
                leaks
                |> List.filter (fun c -> not (guild.CurrentUser.GetPermissions(c).ManageRoles))

            if not stuck.IsEmpty then
                bad $"I can't edit permissions on {some (stuck |> List.map mention)}; give me Manage Roles there."
    ]

let private individualLines (guild: SocketGuild) (settings: LockoutSettings) : string list =
    let own = Hiding.individually guild settings

    [
        if not own.IsEmpty then
            note
                $"{own.Length} channels have permissions of their own or no category, so I set the role on each one instead of syncing it with its category (syncing would erase those permissions): {some (own |> List.map mention)}"
    ]

let private personalLines (guild: SocketGuild) : string list =
    let personal =
        Hiding.channels guild
        |> List.filter (fun c ->
            c.PermissionOverwrites
            |> Seq.exists (fun o -> o.TargetId <> guild.CurrentUser.Id && Members.isPersonalView o))

    [
        if not personal.IsEmpty then
            note
                $"{personal.Length} channels let single members see them through their own overwrites. A lockout takes the member's own away and gives them back at the end."
    ]

let private hierarchyLines (guild: SocketGuild) : string list =
    let bot = Members.botRank guild

    let above =
        guild.Roles
        |> Seq.filter (fun r -> not r.IsEveryone && not r.IsManaged && r.Position >= bot)
        |> Seq.map (fun r -> $"<@&{r.Id}>")
        |> List.ofSeq

    [
        if not above.IsEmpty then
            note $"Members with {some above} can't lock themselves out: those roles are as high as mine or higher."
    ]

/// The report: ✅ fine, ❌ must be fixed, ⚠️ worth a look, ℹ️ for information.
let report
    (guild: SocketGuild)
    (settings: LockoutSettings option)
    (ready: bool)
    (admin: IGuildUser)
    (active: int)
    (overdue: int)
    : string =
    let setup =
        match settings with
        | None ->
            [
                bad "Not set up: set `lockout.role` and `lockout.channel` with /config. /touch-grass is off."
            ]
        | Some settings ->
            match guild.GetRole settings.RoleId with
            | null -> [ bad $"There's no role {settings.RoleId}." ]
            | role ->
                let leaking = channelLines guild settings role

                roleLines guild role
                @ botLines guild
                @ leaking
                @ [
                    if ready then
                        ok "I've hidden every channel; /touch-grass is on."
                    elif leaking |> List.exists (fun l -> l.StartsWith "❌") then
                        warn "/touch-grass refuses to run until the channels above are fixed."
                    else
                        warn
                            "This looks right, but my check (every 30 seconds) hasn't confirmed it yet. Run this again in a minute."
                ]
                @ individualLines guild settings
                @ personalLines guild
                @ hierarchyLines guild
                @ [
                    match Members.obstacle guild settings.RoleId admin with
                    | None -> note "You could lock yourself out."
                    | Some reason -> note $"You couldn't lock yourself out: {reason}"
                ]

    let database =
        [
            note $"Lockouts running: {active}."

            if overdue > 0 then
                warn
                    $"{overdue} lockouts are past their time but not restored yet. I retry every 30 seconds; if this stays, see my log."
        ]

    let lines = setup @ database
    let problems = lines |> List.filter (fun l -> l.StartsWith "❌") |> List.length

    let header =
        if problems = 0 then
            "**Setup looks correct.**"
        else
            $"**{problems} problems found.**"

    String.concat "\n" (header :: lines)
