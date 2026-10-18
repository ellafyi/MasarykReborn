namespace Masaryk.Core

open System.Threading.Tasks
open Masaryk.Configuration
open Masaryk.Postgres

/// The guild's configuration in use. A revision that doesn't load keeps the previous one.
type ConfigurationStore(repository: ConfigurationRepository, guildId: uint64) =
    let mutable state = 0L, Defaults.configuration

    /// The revision in use; 0 when none was saved and the defaults apply.
    member _.Version = fst state
    member _.Current = snd state

    member _.Reload() : Task<unit> =
        task {
            try
                match! repository.Latest guildId with
                | None -> state <- 0L, Defaults.configuration
                | Some(version, document) ->
                    match Document.read document with
                    | Ok(_, configuration) -> state <- version, configuration
                    | Error problems ->
                        let problems = String.concat "; " problems
                        printfn $"Configuration version {version} doesn't load, keeping version {fst state}: {problems}"
            with error ->
                printfn $"Configuration couldn't be read: {error.Message}"
        }
