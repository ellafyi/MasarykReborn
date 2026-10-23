namespace Masaryk.Postgres

open System
open Npgsql

/// Locates this assembly's embedded SQL migrations for the migrator.
type MigrationAssemblyMarker = class end

module Database =
    let connectionString () =
        match Environment.GetEnvironmentVariable "MASARYK_DATABASE_CONNECTION_STRING" with
        | value when String.IsNullOrWhiteSpace value ->
            invalidOp "MASARYK_DATABASE_CONNECTION_STRING environment variable is required."
        | value -> value

    let fromEnvironment () =
        NpgsqlDataSource.Create(connectionString ())
