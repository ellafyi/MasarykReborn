# Masaryk Reloaded Discord Bot

Masaryk Reloaded is a modern rebuild of [MasarykBOT](https://github.com/zloutek1/MasarykBOT).
The eventual goal is feature parity with the original bot but built on F# / .NET 10,
Discord.Net, PostgreSQL, and Docker Compose.
Upgrade to .NET 11 is planned, once a full release happens and dependencies get updated.

## Development
This project is being made with JetBrains Rider. Visual Studio can work, but if any issues arise, you're on your own.
All parts of the bot are launched via a Docker Compose stack. Compose provides a testing Postgres database, the bot itself, as well as all of its parts, including the migration service.

## Deployment
Production runs as a Portainer stack next to the language model container: create a stack from this repository with compose path `compose.portainer.yaml`. It joins the `masaryk` Docker network created by the model's stack, and reads `BOT_TOKEN`, `GUILD_ID`, `POSTGRES_PASSWORD` and `LLM_API_KEY` from the stack's environment variables.

## Planned Features

The plan is to eventually do everything the [old bot](https://github.com/zloutek1/MasarykBOT/tree/main/bot/cogs) did.
Commands don't have to work exactly the same way, they just need to cover the same stuff.

- [x] Starboard: set up in a JSON config (source channels, threshold, target channel). Emoji counts in starboard messages update as reactions change. Reply chains show context of the discussion preceding the message that entered the starboard.
- [x] Bookmarks: 🔖 reaction to any message in the server sends a copy of it to the user's DMs, alongside a direct link to jump to the original message.
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
