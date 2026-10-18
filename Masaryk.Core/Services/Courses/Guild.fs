/// Course channel changes on a Discord server.
module Masaryk.Core.Services.Courses.Guild

open System.Threading.Tasks
open System
open Discord
open Masaryk.Postgres

/// Link every unlinked channel named after an unlinked catalog course.
let matchChannels (repository: CourseRepository) (home: string option) (guild: IGuild) : Task<Channels.Matching> =
    task {
        let! courses = repository.All()
        let! links = repository.Links()
        let! channels = guild.GetTextChannelsAsync()
        let linkedChannels = links |> List.map snd |> Set.ofList

        let candidates =
            channels
            |> Seq.filter (fun c -> not (c :? IVoiceChannel) && not (linkedChannels.Contains c.Id))
            |> Seq.map (fun c -> c.Id, c.Name)
            |> Seq.toList

        let existing = channels |> Seq.map _.Id |> Set.ofSeq

        // A link to a deleted channel doesn't hold the course.
        let linkedCourses =
            links |> List.filter (snd >> existing.Contains) |> List.map fst |> Set.ofList

        let matching = Channels.matching home courses linkedCourses candidates

        for course, channel in matching.Links do
            do! repository.Link(course.Id, channel)

        return matching
    }

/// The category a course channel belongs in: its current one when that is already one of the
/// course's categories, else the first with room, created when missing.
let private categoryFor
    (home: string option)
    (guild: IGuild)
    (course: Course)
    (current: Nullable<uint64>)
    : Task<ICategoryChannel> =
    task {
        let wanted = Channels.category home course
        let! categories = guild.GetCategoriesAsync()

        match
            categories
            |> Seq.tryFind (fun c -> Nullable c.Id = current && Channels.isOneOf wanted c.Name)
        with
        | Some category -> return category
        | None ->
            let! channels = guild.GetChannelsAsync()

            let sizes =
                categories
                |> Seq.map (fun category ->
                    let inside =
                        channels
                        |> Seq.filter (fun c ->
                            match c with
                            | :? INestedChannel as nested -> nested.CategoryId = Nullable category.Id
                            | _ -> false)

                    category.Name, Seq.length inside)
                |> Map.ofSeq

            let name = Channels.roomIn wanted sizes

            match categories |> Seq.tryFind (fun c -> c.Name = name) with
            | Some category -> return category
            | None -> return! guild.CreateCategoryAsync name
    }

type Creation =
    | Created of ITextChannel
    | Exists of uint64

/// Create the course's channel in its category, opening a numbered category when it is full.
let create (repository: CourseRepository) (home: string option) (guild: IGuild) (course: Course) : Task<Creation> =
    task {
        let! linked = repository.ChannelOf course.Id

        let! existing =
            match linked with
            | Some id -> guild.GetChannelAsync id
            | None -> Task.FromResult null

        match existing with
        | null ->
            let! category = categoryFor home guild course (Nullable())

            let! channel =
                guild.CreateTextChannelAsync(
                    Channels.name home course,
                    fun properties ->
                        properties.CategoryId <- Optional(Nullable category.Id)
                        properties.Topic <- Channels.topic course
                )

            do! repository.Link(course.Id, channel.Id)
            return Created channel
        | channel -> return Exists channel.Id
    }

/// Link the course to the channel and move the channel into the course's category.
/// Returns the category it moved to, or None if it was already in place.
let link (repository: CourseRepository) (home: string option) (guild: IGuild) (course: Course) (channelId: uint64) =
    task {
        do! repository.Link(course.Id, channelId)
        let! channel = guild.GetChannelAsync channelId

        match channel with
        | :? INestedChannel as nested ->
            let! category = categoryFor home guild course nested.CategoryId

            if nested.CategoryId = Nullable category.Id then
                return None
            else
                do! nested.ModifyAsync(fun properties -> properties.CategoryId <- Optional(Nullable category.Id))
                return Some category
        | _ -> return None
    }
