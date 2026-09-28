open System
open DbUp
open Masaryk.Postgres

[<EntryPoint>]
let main _ =
    let connectionString = Environment.GetEnvironmentVariable("MASARYK_DATABASE_CONNECTION_STRING")

    if String.IsNullOrWhiteSpace connectionString then
        eprintfn "MASARYK_DATABASE_CONNECTION_STRING is missing or empty."
        1
    else
        try
            let upgrader =
                DeployChanges.To
                    .PostgresqlDatabase(connectionString)
                    .WithScriptsEmbeddedInAssembly(
                        typeof<MigrationAssemblyMarker>.Assembly,
                        Func<string, bool>(_.Contains(".Migrations.")))
                    .WithTransactionPerScript()
                    .Build()

            let result = upgrader.PerformUpgrade()

            if result.Successful then
                printfn "Database migrations completed successfully."
                0
            else
                eprintfn "Database migration failed. Check database connectivity and migration state."
                1
        with _ ->
            eprintfn "Database migration failed. Check database connectivity and configuration."
            1
