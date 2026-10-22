# Masaryk

Discord bot for one server (`GUILD_ID`), written in F# on .NET 10 with Discord.Net and PostgreSQL. Features: starboard, bookmarks, leaderboards, course channels, lunch menus, fun commands, and chat replies from a self-hosted language model.

## Commands

```shell
dotnet tool restore                          # pins Fantomas 8.0.7
git config --local core.hooksPath .githooks  # once per clone
dotnet build Masaryk.sln
dotnet fantomas Masaryk.Core Masaryk.Configuration Masaryk.Postgres Masaryk.Postgres.Migrator
docker compose up --build -d                 # dev stack: bot, migrator, Postgres (published on 15432)
docker compose logs -f masaryk.core
```

- The pre-commit hook formats staged F# files with Fantomas, then builds the whole solution. A commit fails if either step fails or if the formatter changed files.
- There is no test project. Verify by building and running the bot against a test guild.
- One-off imports run in the bot image: `docker compose run --rm masaryk.core import-history` and `... import-courses <html>`.

## Layout

| Project | Role |
|---|---|
| `Masaryk.Core` | Bot host. `Program.fs` wires features; each feature lives in `Services/<Name>/` |
| `Masaryk.Postgres` | Repositories (Npgsql, hand-written SQL in `Sql.fs` helpers) and `Migrations/*.sql` |
| `Masaryk.Postgres.Migrator` | Runs the migrations with DbUp; Compose runs it before the bot starts |
| `Masaryk.Configuration` | Per-server settings document (`/config export` and `/config import`); depends on nothing else |

## Conventions

- F# compiles in file order. New files go into the `.fsproj` after everything they depend on.
- A feature is `Context -> Task<Command list>`. It subscribes to the client events it needs and returns its slash commands. Add it to `features` in `Program.fs`.
- Admin-only commands set `Permission = Some GuildPermission.Administrator`. `Program.fs` checks it again on every call, so Discord's per-server overrides don't widen access.
- Slash commands are registered on the one guild only; handlers for events must ignore other guilds.
- Gateway handlers don't do slow work: queue it through `Services/Shared/Worker.fs`.
- Migrations are numbered `NNN_name.sql`, run in order, one transaction each. Never edit an applied one; add a new one, and add it as an `EmbeddedResource` in `Masaryk.Postgres.fsproj`. Scripts are listed by hand, so an unlisted file is silently never run.
- Settings come from environment variables: `BOT_TOKEN`, `GUILD_ID`, `MASARYK_DATABASE_CONNECTION_STRING`, optional `LLM_API_URL`, `LLM_API_KEY`, `LLM_MODEL`. Compose builds the connection string from the `POSTGRES_*` variables.
- Commit messages follow `type: summary` (`feat:`, `fix:`, `refactor:`, `build:`).

## Chat feature

- The language model is an OpenAI-compatible server (`/v1/chat/completions`), not instruction-tuned. Send only `user` (with a neutral `name`) and `assistant` messages; it rejects `system` messages, streaming and tools.
- The model can repeat memorized private training chat. Keep its weights, tokenizers and training data out of the repository, and send Discord usernames and IDs to it only as neutral names (`u1`, `u2`), as `Chat.build` does.

## Deployment

- `compose.yaml` is for local development. `compose.portainer.yaml` is the Portainer stack: it builds from Git, takes variables from the Portainer UI, publishes no ports, and joins the external Docker network `masaryk` created by the model's stack. Deploy the model stack first.
- Never publish Postgres or the model API on a public interface.

## Don't

- Don't commit `.env`, tokens, database dumps, or `db-data/`.
- Don't commit `docs/` or `Masaryk.Postgres/README.md`: they are local-only notes excluded in `.git/info/exclude`. Don't force-add them or link to them from tracked files.
- Don't reformat files you didn't change; Fantomas may touch unrelated files, so revert those.
