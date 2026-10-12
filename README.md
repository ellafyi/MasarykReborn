# Masaryk Reloaded Discord Bot

Masaryk Reloaded is a modern rebuild of [MasarykBOT](https://github.com/zloutek1/MasarykBOT).
The eventual goal is feature parity with the original bot, built on F# / .NET 10,
Discord.Net, PostgreSQL, and Docker Compose rather than porting its Python cog architecture.

## Development
This project was made entirely within JetBrains Rider. Visual Studio can work, but if any issues arise, you're on your own.
All parts of the bot are launched via a Docker Compose stack. Compose provides a testing Postgres database, the bot itself, as well as all of its parts, including the migration service.

## Planned Features

The [original bot's cogs](https://github.com/zloutek1/MasarykBOT/tree/main/bot/cogs)
define the feature-parity baseline. Checked items are implemented in this rebuild;
unchecked items are planned, not currently available. This is a scope checklist,
not an implementation order or a commitment to preserve every command name.

### Implemented

- [x] **Configurable starboard** — multiple boards, reaction thresholds, source-channel rules, persistent message snapshots, and archive publication.

### Legacy feature parity

- [ ] **Remaining legacy starboard behavior** — reply-chain context, channel/emoji/spoiler-sensitive thresholds, and dedicated best-of-memes and best-of-bot routing.
- [ ] **Channel and user leaderboards** — message-based user rankings, channel/thread inclusion and exclusion filters, top users, and rankings around a selected member.
- [ ] **Message logging and backups** — scheduled and administrator-triggered history collection, including messages, attachments, reactions, emoji usage, and server/channel/thread/member/role metadata.
- [ ] **Course management** — MUNI faculty and course imports, search and autocomplete, course information, join/leave/leave-all registration, student course profiles, on-demand course channels and access management, registration-channel guidance, and registration recovery from existing channels.
- [ ] **Verification and onboarding** — reaction-based verified-role assignment and removal, welcome DMs, and rules-channel setup and updates.
- [ ] **Self-service role and channel menus** — reaction-driven role assignment/removal and channel visibility, with menu updates.
- [ ] **Personal bookmarks** — save a message to DMs with its source link and attachment preview, then delete the saved bookmark.
- [ ] **Automatic threads** — create threads for new messages in configured channels, deriving titles from message text or linked pages.
- [ ] **Markov text generation** — learn from backed-up server messages, generate text, continue a supplied prompt, retrain on demand, and respond to bot mentions.
- [ ] **Fun and media utilities** — emoji lists and images/URLs, server icons and banners, user avatars, random choices and yes/no answers, hugs, night-sky art, HTTP cats, and emoji-to-Braille art.
- [ ] **Code evaluation** — external compilation/execution of C, C++, Python, Haskell, and APL snippets, with results returned in Discord.
- [ ] **Help and information** — paginated command/category help, command usage, uptime, latency, invite links, and server/category/channel/member statistics.
- [ ] **Administration and operations** — administrator message posting and purging, command synchronization, owner-only shutdown and presence controls, log retrieval, and feature-module listing/load/unload/reload equivalents.
- [ ] **Command error handling** — actionable input, permission, and cooldown feedback, plus diagnostic reporting to configured error channels.

Parity means equivalent capabilities on the new platform, not a literal copy of
Python cogs, external service integrations, or legacy implementation quirks.

### Beyond legacy parity

- [ ] **LLM conversation integration** — a new conversational capability, separate from the original bot's Markov generator.
