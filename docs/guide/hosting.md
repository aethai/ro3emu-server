# Play with friends and host a server

**On Windows.** The host runs `RO3Emu.exe host`; it prints the address to share (Windows asks once whether the server
may use the network: allow it). Friends run `RO3Emu.exe join <that address>`. For friends outside your home network,
forward TCP ports `5380`, `20000` and `33100` on your router to the host and share your public IP. Party voice chat
is off unless the host adds `--voice-port 33101` (any free port); then forward that port too.

**On Linux.** Download **`RO3Emu-server-linux-x64.tar.gz`**, extract it and run `server.sh`; nothing to install:

```bash
./server.sh --client /path/to/ROO_PC --host <public IP> --gm <your account>     # a game folder copied from Windows
./server.sh --download /srv/ro3/ROO_PC --host <public IP> --gm <your account>   # or only the files it needs, from Genesis
./server.sh --service ...                                                       # install it as a systemd service
```

Open TCP `5380`, `20000` and `33100`, and the voice port if you turn voice on with `--voice-port 33101`.

**Good to know**
- **GM commands** (`!help` in chat) are on for every account when you play on your own PC. A hosted server gives them
  only to the accounts you name with `--gm` ([GM access](gm-access.md)).
- **Web panel:** a website for your players (account, characters, who's online, rankings) and for you (accounts,
  bans, server numbers) on port `5381`. Open that port too, or put it behind https ([web panel](panel.md)). Admins are
  the accounts you name with `--gm`. `--panel-port <n>` moves it, `--no-panel` turns it off (RO3Emu.exe and
  `server.sh`).
- **Bug reports from players:** the Feedback button in the game menu or `/bug <text>` in chat ([feedback](feedback.md)), optionally posted to a Discord channel.
- **Monsters that attack on sight as in RO1** (on by default, as a trial): set `Ro3Emu__Ro1AggressiveMonsters=false` (or
  `"Ro1AggressiveMonsters": false` under `Ro3Emu` in `appsettings.json`) to turn it off. The field monsters whose Ragnarok Online 1
  counterpart is aggressive then attack players they see, like the few that already do. This follows RO1
  (rAthena's monster database), not the original RO3 server. RO3 mixes levels on one map (Payon Forest: Greatest
  Generals at 55 among Horns at 16, on the level 16 story's path), so such a monster stays passive, as in the client,
  on an open field whose own spawns hold a common monster (at least 10% of them) more than
  `Ro3Emu__Ro1AggressiveMaxLevelGap` levels below it (default 15; `0` turns the gate off). Dungeons, towns, Territory
  War sandboxes, story and team rooms, bosses and MVPs, and monsters a map's spawns do not place keep the RO1 behaviour.
- **Backups:** the database is backed up to `data/backups/` at start and every 6 hours (newest 20 kept). All but the
  newest two are gzip-compressed (`.db.gz`, about a quarter of the size); `Ro3Emu.Server --restore-backup` takes either
  kind. `Ro3Emu__Backup__Keep`, `Ro3Emu__Backup__IntervalHours` and `Ro3Emu__Backup__Uncompressed` (or `Backup` under
  `Ro3Emu` in `appsettings.json`) change the three numbers; `Uncompressed` equal to `Keep` compresses nothing.
- **Database size:** every reward, purchase and activity writes a receipt so a lost or repeated request is never paid
  twice. Receipts older than 2 days are trimmed to their hashes every hour, in small batches
  (`Ro3Emu__ReceiptRetention__Days`, `__BatchSize`, `__IntervalMinutes`; `Days` 0 keeps them whole). The space freed
  is reused, so the file stops growing; to shrink it on disk, stop the server and run `VACUUM` on `data/ro3emu.db`
  once (`sqlite3 data/ro3emu.db "VACUUM"`).
- **Test realm switches** (not the original game, off by default): `Ro3Emu__Testing__AlwaysOpen=true` opens every
  To-Do activity at any hour of any weekday (Survivor Battle, Dragon Coffin Island, Ilmata, the bonfire, Festival
  Invitation, Wedding Crashers, Hunting Challenge, Mock Battle, the Commendation Vault...), each repeating back to back
  with its own length. `Ro3Emu__Testing__UnlimitedRewards=true` lets the weekly and daily reward counts never run out:
  Realm of the Gods and Phantom Realm rewards, MVP rewards, the Commendation Vault's extra awards and Dodo's Request.
  (`"Testing": { "AlwaysOpen": true }` under `Ro3Emu` in `appsettings.json` does the same.) The server warns at startup
  while one is on. A GM turns them on or off without a restart: `!testing alwaysopen on|off`, `!testing unlimited
  on|off`, `!testing reset`, `!testing` to show them; that change lasts until the next start. The Guild League and the
  Territory Wars keep their calendars (`!league`, `!siege open`, `!twpoint`). Details:
  [activities](../systems/activities.md#test-realm-switches-ro3emutesting).
- **One session per account:** a second login takes over from the first. The client runs one instance per PC;
  `Ro3Emu.BotCli` can stand in for extra players when you test alone.

| Hardware | Minimum | Measured target (500 players) |
|---|---|---|
| CPU | 2 cores | at most 2 cores used by the server |
| RAM | 2 GB | at most 2 GB working set |
| Disk | 3 GB plus the game | SSD recommended |

## Purchase links

The game's recharge, shop top-up and monthly-card buttons open Genesis' payment page. RO3Emu never sends a
player there: a payment made there reaches Genesis, not your server. Without any option the buttons open
nothing. To send players to your own page (donations, a store), give its address every time you patch the game:

```
RO3Emu.exe join 203.0.113.10 --purchase-url https://example.com/donate
```

`start` and `host` take the option too. Only an `http` or `https` address is accepted. The patcher writes it into the
client hooks it installs; the redirect sits on `UnityEngine.Application.OpenURL`, the call every one of those buttons
ends in (`OpenUrlPatch`, `Ro3Emu.ClientHooks.UrlRewrite`).

