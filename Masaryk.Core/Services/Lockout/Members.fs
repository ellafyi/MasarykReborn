/// Taking a member's roles away for a lockout and giving them back.
module Masaryk.Core.Services.Lockout.Members

open System
open System.Collections.Generic
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Postgres

/// Discord doesn't let anyone remove managed roles (boosts, bots, integrations).
let private removable (guild: SocketGuild) (roleId: uint64) =
    match guild.GetRole roleId with
    | null -> false
    | role -> not role.IsManaged

/// The member's roles without @everyone, which Discord adds on its own.
let private held (guild: SocketGuild) (user: IGuildUser) =
    user.RoleIds |> Seq.filter (fun id -> id <> guild.Id) |> List.ofSeq

/// The highest position among these roles. Discord compares it to decide who may change whom.
let rank (guild: SocketGuild) (roles: seq<uint64>) =
    roles
    |> Seq.fold
        (fun top id ->
            match guild.GetRole id with
            | null -> top
            | role -> max top role.Position)
        0

let botRank (guild: SocketGuild) : int =
    rank guild (guild.CurrentUser.Roles |> Seq.map _.Id)

/// The roles a lockout takes away.
let taken (guild: SocketGuild) (user: IGuildUser) : uint64 list =
    held guild user |> List.filter (removable guild)

/// Why the bot can't lock this member out, if it can't.
let obstacle (guild: SocketGuild) (lockRole: uint64) (user: IGuildUser) : string option =
    let bot = botRank guild

    if user.Id = guild.OwnerId then
        Some "The server owner can't be locked out: Discord doesn't let bots change the owner's roles."
    elif rank guild user.RoleIds >= bot then
        Some
            "I can't change your roles because one of them is as high as mine or higher. An admin has to move my role above yours."
    elif (guild.GetRole lockRole).Position >= bot then
        Some "The lockout role is above my own role, so I can't give it out. An admin has to fix the role order."
    else
        None

let private setRoles (user: IGuildUser) (roles: uint64 list) : Task =
    user.ModifyAsync(fun properties -> properties.RoleIds <- Optional<IEnumerable<uint64>>(roles))

/// Replace the member's roles with the lockout role, keeping only roles that can't be removed.
let lock (guild: SocketGuild) (lockRole: uint64) (user: IGuildUser) : Task =
    let kept = held guild user |> List.filter (removable guild >> not)
    setRoles user (lockRole :: kept)

/// Whether the member joined again after the lockout began, which drops every role.
let rejoined (user: IGuildUser) (locked: LockedMember) : bool =
    user.JoinedAt.HasValue
    && user.JoinedAt.Value > locked.LockedAt
    && not (Seq.contains locked.LockRole user.RoleIds)

/// Swap the lockout role for the taken roles. Skips roles that are gone or now above the bot,
/// since Discord would refuse the whole change.
let restore (guild: SocketGuild) (user: IGuildUser) (locked: LockedMember) : Task =
    let bot = botRank guild

    let back, lost =
        locked.Roles
        |> List.partition (fun id -> removable guild id && (guild.GetRole id).Position < bot)

    if not lost.IsEmpty then
        eprintfn $"Touch grass: roles {lost} of {locked.UserId} no longer exist or are above mine; not restored."

    let current = held guild user |> List.filter ((<>) locked.LockRole)
    setRoles user (List.distinct (current @ back))
