/// Hides every channel from the lockout role except the timeout channel, which becomes read-only.
/// Categories get the deny and their synced channels follow. A channel with permissions of its
/// own gets the deny directly, because syncing would erase them.
module Masaryk.Core.Services.Lockout.Hiding

open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Configuration

let private hidden =
    OverwritePermissions.InheritAll.Modify(viewChannel = PermValue.Deny)

let private readOnly =
    OverwritePermissions.InheritAll.Modify(
        viewChannel = PermValue.Allow,
        readMessageHistory = PermValue.Allow,
        sendMessages = PermValue.Deny,
        addReactions = PermValue.Deny,
        useApplicationCommands = PermValue.Deny,
        createPublicThreads = PermValue.Deny,
        createPrivateThreads = PermValue.Deny,
        sendMessagesInThreads = PermValue.Deny
    )

let private same (current: OverwritePermissions) (wanted: OverwritePermissions) =
    current.AllowValue = wanted.AllowValue && current.DenyValue = wanted.DenyValue

/// Every channel except threads, which follow their parent.
let channels (guild: SocketGuild) : SocketGuildChannel list =
    guild.Channels
    |> Seq.filter (fun c -> not (box c :? IThreadChannel))
    |> List.ofSeq

let private isCategory (channel: SocketGuildChannel) = box channel :? SocketCategoryChannel

let private has (wanted: OverwritePermissions) (role: SocketRole) (channel: SocketGuildChannel) =
    let current = channel.GetPermissionOverwrite(role)
    current.HasValue && same current.Value wanted

let private parent (guild: SocketGuild) (channel: SocketGuildChannel) : SocketCategoryChannel option =
    match box channel with
    | :? INestedChannel as nested when nested.CategoryId.HasValue ->
        guild.GetCategoryChannel nested.CategoryId.Value |> Option.ofObj
    | _ -> None

/// The channel's overwrites, except the lockout role's.
let private others (role: SocketRole) (channel: SocketGuildChannel) =
    channel.PermissionOverwrites
    |> Seq.filter (fun o -> o.TargetId <> role.Id)
    |> Seq.map (fun o -> o.TargetId, o.TargetType, o.Permissions.AllowValue, o.Permissions.DenyValue)
    |> Set.ofSeq

/// Whether syncing the channel with its category would change only the lockout role's overwrite.
let private followsCategory (guild: SocketGuild) (role: SocketRole) (channel: SocketGuildChannel) =
    match parent guild channel with
    | Some category -> others role channel = others role category
    | None -> false

/// Channels that get the lockout role's overwrite directly instead of through their category.
let individually (guild: SocketGuild) (settings: LockoutSettings) : SocketGuildChannel list =
    let role = guild.GetRole settings.RoleId

    channels guild
    |> List.filter (fun c ->
        not (isCategory c)
        && c.Id <> settings.ChannelId
        && not (followsCategory guild role c))

type Step =
    | Leave
    | Set
    /// Copy the category's permissions onto the channel.
    | Sync

/// Sync only a channel that follows its already hidden category. Syncing any other would erase
/// its own permissions.
let step (alreadyRight: bool) (isTimeout: bool) (canSync: bool) : Step =
    if alreadyRight then Leave
    elif isTimeout then Set
    elif canSync then Sync
    else Set

type Outcome =
    {
        Changed: int
        /// Channel IDs that couldn't be changed, with Discord's reason.
        Failures: (uint64 * string) list
    }

/// Bring every category and channel in line: the deny on categories first, then the channels.
let reconcile (guild: SocketGuild) (settings: LockoutSettings) : Task<Outcome> =
    task {
        let role = guild.GetRole settings.RoleId
        let failures = ResizeArray<uint64 * string>()
        let changed = ref 0

        let attempt (channel: SocketGuildChannel) (change: unit -> Task) : Task =
            task {
                changed.Value <- changed.Value + 1

                try
                    do! change ()
                with error ->
                    failures.Add(channel.Id, error.Message)
            }

        let everything = channels guild
        let categories, children = everything |> List.partition isCategory

        for category in categories do
            if not (has hidden role category) then
                do! attempt category (fun () -> category.AddPermissionOverwriteAsync(role, hidden))

        for channel in children do
            let isTimeout = channel.Id = settings.ChannelId
            let wanted = if isTimeout then readOnly else hidden

            let canSync =
                followsCategory guild role channel
                && (parent guild channel |> Option.exists (has hidden role))

            match step (has wanted role channel) isTimeout canSync with
            | Leave -> ()
            | Set -> do! attempt channel (fun () -> channel.AddPermissionOverwriteAsync(role, wanted))
            | Sync -> do! attempt channel (fun () -> (box channel :?> INestedChannel).SyncPermissionsAsync())

        return
            {
                Changed = changed.Value
                Failures = List.ofSeq failures
            }
    }
