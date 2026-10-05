namespace Masaryk.Postgres

open System
open Npgsql

/// Locates this assembly's embedded SQL migrations for the migrator.
type MigrationAssemblyMarker = class end

module Database =
    let fromEnvironment () =
        let connectionString =
            Environment.GetEnvironmentVariable("MASARYK_DATABASE_CONNECTION_STRING")

        if String.IsNullOrWhiteSpace connectionString then
            invalidOp "MASARYK_DATABASE_CONNECTION_STRING environment variable is required."

        NpgsqlDataSource.Create(connectionString)
