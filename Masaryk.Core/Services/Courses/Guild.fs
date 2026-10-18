/// Course channel changes on a Discord server.
module Masaryk.Core.Services.Courses.Guild

open System.Threading.Tasks
open System
open Discord
open Masaryk.Postgres

/// Link every unlinked channel named after an unlinked catalog course.
let matchChannels (repository: CourseRepository) (guild: IGuild) : Task<Channels.Matching> =
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

        let matching = Channels.matching courses linkedCourses candidates

        for course, channel in matching.Links do
            do! repository.Link(course.Id, channel)

        return matching
    }

type Creation =
    | Created of ITextChannel
    | Exists of uint64

/// Create the course's channel in its category, opening a numbered category when it is full.
let create (repository: CourseRepository) (guild: IGuild) (course: Course) : Task<Creation> =
    task {
        let! linked = repository.ChannelOf course.Id

        let! existing =
            match linked with
            | Some id -> guild.GetChannelAsync id
            | None -> Task.FromResult null

        match existing with
        | null ->
            let! assigned = repository.CategoryOverride(course.Id, course.Code)
            let! categories = guild.GetCategoriesAsync()
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

            let name =
                Channels.roomIn (assigned |> Option.defaultValue (Channels.category course)) sizes

            let! category =
                match categories |> Seq.tryFind (fun c -> c.Name = name) with
                | Some category -> Task.FromResult category
                | None -> guild.CreateCategoryAsync name

            let! channel =
                guild.CreateTextChannelAsync(
                    Channels.name course,
                    fun properties ->
                        properties.CategoryId <- Optional(Nullable category.Id)
                        properties.Topic <- Channels.topic course
                )

            do! repository.Link(course.Id, channel.Id)
            return Created channel
        | channel -> return Exists channel.Id
    }
