open System
open DbUp
open Masaryk.Postgres

[<EntryPoint>]
let main _ =
    let result =
        DeployChanges.To
            .PostgresqlDatabase(Database.connectionString ())
            .WithScriptsEmbeddedInAssembly(
                typeof<MigrationAssemblyMarker>.Assembly,
                Func<string, bool>(_.Contains(".Migrations."))
            )
            .WithTransactionPerScript()
            .Build()
            .PerformUpgrade()

    if result.Successful then
        printfn "Database migrations completed successfully."
        0
    else
        let script =
            if isNull result.ErrorScript then
                ""
            else
                $" in {result.ErrorScript.Name}"

        eprintfn $"Database migration failed{script}: {result.Error.Message}"
        1
