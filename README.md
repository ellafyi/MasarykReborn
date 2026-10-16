# Masaryk Reloaded Discord Bot

Masaryk Reloaded is a modern rebuild of [MasarykBOT](https://github.com/zloutek1/MasarykBOT).
The eventual goal is feature parity with the original bot, built on F# / .NET 10,
Discord.Net, PostgreSQL, and Docker Compose rather than porting its Python cog architecture.

## Development
This project was made entirely within JetBrains Rider. Visual Studio can work, but if any issues arise, you're on your own.
All parts of the bot are launched via a Docker Compose stack. Compose provides a testing Postgres database, the bot itself, as well as all of its parts, including the migration service.

## Planned Features

The plan is to eventually do everything the [old bot](https://github.com/zloutek1/MasarykBOT/tree/main/bot/cogs) did.
Commands don't have to work exactly the same way, they just need to cover the same stuff.

- [x] Starboard: as many boards as you want, set up in JSON (source channels, threshold, target channel). Posted counts update as reactions change, and replies show what they were replying to.
- [x] Bookmarks: react with 🔖 to get a copy of a message in your DMs, react with 🗑️ on the copy to delete it
- [x] Chat with a custom-trained language model (replacing the old Markov chain): @mention the bot or reply to one of its model replies, and it answers using the last few messages as context

From the old bot:

- [ ] Leaderboards for users and channels
- [ ] Message logging and backups
- [ ] Course channels: search MUNI courses, join/leave them, get access to their channels
- [ ] Verification, welcome DMs and the rules channel
- [ ] Role menus (pick roles and channels by reacting)
- [ ] Auto-created threads in some channels
- [ ] Fun commands: emoji, avatars, server icon/banner, choose, hug, http.cat, emoji to ASCII art, etc.
- [ ] Running code snippets (C, C++, Python, Haskell, APL)
- [ ] Help, ping, uptime, server info
- [ ] Admin commands (say, purge, sync, shutdown, logs)
- [ ] Proper error messages, plus an error log channel
