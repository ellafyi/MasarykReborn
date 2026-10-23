# Masaryk Reloaded Discord Bot

Masaryk Reloaded is a modern rebuild of [MasarykBOT](https://github.com/zloutek1/MasarykBOT).
The eventual goal is feature parity with the original bot but built on F# / .NET 10,
Discord.Net, PostgreSQL, and Docker Compose.
Upgrade to .NET 11 is planned, once a full release happens and dependencies get updated.

## Development
This project is being made with JetBrains Rider. Visual Studio can work, but if any issues arise, you're on your own.
All parts of the bot are launched via a Docker Compose stack. Compose provides a testing Postgres database, the bot itself, as well as all of its parts, including the migration service.

### Getting started

1. Create a Discord application with a bot user, enable the **Message Content Intent**, and invite it to a test server.
2. Copy `.env.example` to `.env` and fill in `BOT_TOKEN`, `GUILD_ID` (the test server's ID) and `POSTGRES_PASSWORD`. Leave `LLM_API_URL` empty unless you run the language model.
3. Set up the tools and the Git hook once per clone:
   ```shell
   dotnet tool restore
   git config --local core.hooksPath .githooks
   ```
4. Start everything with `docker compose up --build -d` and follow the bot with `docker compose logs -f masaryk.core`. Postgres is published on port 15432 for a database client.

The pre-commit hook formats staged F# files with Fantomas and builds the solution; the commit fails if either fails or if formatting changed a file. There is no test project: check a change by running the bot against your test server.

## Repo structure

| Project | What it holds |
|---|---|
| `Masaryk.Core` | The bot. `Program.fs` connects to Discord, starts every feature and routes slash commands. |
| `Masaryk.Postgres` | Database access: one repository per feature, the shared helpers in `Sql.fs`, and the SQL migrations in `Migrations/`. |
| `Masaryk.Postgres.Migrator` | Applies the migrations with DbUp. Compose runs it before the bot starts. |
| `Masaryk.Configuration` | The per-server settings document behind `/config export` and `/config import`. It depends on nothing else. |

### Inside `Masaryk.Core`

- `Program.fs` builds the Discord client and holds the `features` list. Every feature is started from there.
- `Command.fs` defines a slash command (`Command`), what a feature receives at startup (`Context`) and the shape of a feature (`Feature`).
- `Options.fs` builds slash command options and reads what the user filled in.
- `ConfigurationStore.fs` keeps the current server configuration and reloads it when someone saves a new one.
- `Services/<Feature>/` holds one feature each; small features are a single file (`Bookmarks.fs`, `Chat.fs`, `Config.fs`).
- `Services/Shared/` holds helpers used by several features:

| Module | Used for |
|---|---|
| `Worker` | A queue that handles Discord events one at a time, away from the gateway |
| `Messages` | Whether a message was written by a member of this server; a thread's parent channel |
| `Replies` | Following a message's reply chain |
| `MessageEmbed` | Rendering a Discord message as an embed (starboard posts, bookmarks) |
| `Pager` | ◀ ▶ buttons for paged replies |
| `Paging` | Reading long message histories through Discord's rate limits |
| `Period` | The "all time / this year / this month / this week" option |
| `Cooldown` | Limiting how often one person can use a command |
| `Html` | Turning HTML into plain text |

### How a feature works

A feature is a function `Context -> Task<Command list>`. It subscribes to the Discord events it needs and returns its slash commands. Structure shown on the Leaderboard feature:

| File | Role |
|---|---|
| `Leaderboard/Feature.fs` | Creates the repository, subscribes to events and returns the command |
| `Leaderboard/Counting.fs` | Counts every new message, queued through `Worker` |
| `Leaderboard/Ranking.fs` | Builds the embeds |
| `Leaderboard/SlashCommand.fs` | The `/leaderboard` command |
| `Masaryk.Postgres/LeaderboardRepository.fs` | The SQL |
| `Migrations/006_leaderboard.sql` | The tables |

Rules every feature follows:

- Event handlers return right away and queue slow work (database, Discord REST calls) through `Worker`, so the gateway connection never waits.
- The bot serves one server, `GUILD_ID`. Commands are registered only there, and event handlers ignore other servers.
- Admin-only commands set `Permission = Some GuildPermission.Administrator`. `Program.fs` checks it again on every use, because server settings can widen who sees a command.
- Settings a server admin should change go into `Masaryk.Configuration` (the model, its defaults and the document format), not into environment variables.

### Adding a feature

1. Create `Masaryk.Core/Services/<Name>/` with a `Feature.fs` that has `let start: Feature`, and list its files in `Masaryk.Core.fsproj` after the shared helpers.
2. Add `<Name>.Feature.start` to `features` in `Program.fs`.
3. If it stores data, add a repository to `Masaryk.Postgres` and a migration `Migrations/NNN_name.sql`, and list the migration as an `EmbeddedResource` in `Masaryk.Postgres.fsproj`. Migrations are listed by hand: an unlisted file is never run. Never change the SQL of a migration that has been applied anywhere; add a new one.
4. Start the stack, try the feature on your test server, and describe it under *Planned Features* below.

### Style

- Fantomas formats the code; don't format by hand or reformat files you didn't change.
- Use `///` to document a declaration (module, type, function, member) and `//` for notes inside a function. Comment only what the code can't say: a reason, a Discord quirk, a limit.
- Commit messages are `type: summary`, e.g. `feat: add /menu` or `fix: keep the reply chain under the embed limit`.

## Deployment
Production runs as a Portainer stack (but should just work through compose) next to the language model container: create a stack from this repository with compose path `compose.portainer.yaml`. It joins the `masaryk` Docker network created by the model's stack, and reads `BOT_TOKEN`, `GUILD_ID`, `POSTGRES_PASSWORD` and `LLM_API_KEY` from the stack's environment variables.

## Planned Features

The plan is to eventually do everything the [old bot](https://github.com/zloutek1/MasarykBOT/tree/main/bot/cogs) did.
Commands don't have to work exactly the same way, they just need to cover the same stuff.

- [x] Starboard: set up in a JSON config (source channels, threshold, target channel). Emoji counts in starboard messages update as reactions change. Reply chains show context of the discussion preceding the message that entered the starboard.
- [x] Bookmarks (not needed, as discord now apparently has this feature built-in): 🔖 reaction to any message in the server sends a copy of it to the user's DMs, alongside a direct link to jump to the original message.
- [x] Chat with a custom-trained language model (replacing the old bot's Markov chain implementation): @mention the bot or reply to one of its model replies, and it answers using the last few messages as context. The model is not public and neither is its training data. The bot can use any OpenAI compatible API to serve responses.

From the old bot:

- [x] Leaderboards for users and channels
- [ ] Message logging and backups
- [ ] Course channels: search MUNI courses, join/leave them, get access to their channels (catalog import and channel creation done)
- [ ] Verification, welcome DMs and the rules channel
- [ ] Role menus (pick roles and channels by reacting)
- [ ] Auto-created threads in some channels
- [x] Fun commands: emoji, avatars, server icon/banner, http.cat
- [ ] Running code snippets (C, C++, Python, Haskell, APL)
- [ ] Help, ping, uptime, server info
- [ ] Admin commands (say, purge, sync, shutdown, logs)
- [ ] Proper error messages, plus an error log channel
