# Launcher and client distribution

Players can get one file, `Ro3EmuLauncher.exe` (`src/Ro3Emu.Launcher`, .NET 10 WPF + WebView2, self-contained). It
installs the RO3 client patched for your server, keeps it updated, updates itself and starts `ro3.exe` with the launch
proof. The Genesis launcher cannot be reused for this: it pins Genesis' manifest signing key and update
hosts in the exe, update themselves, and their Repair restores the original DLLs.

The same exe runs in one of two modes, chosen by its `launcher.json`:

- **RO3Emu mode** (`"mode": "ro3emu"`, the built-in default, and what the release zip ships): RO3Emu's own app, the
  window over what `RO3Emu.exe` does. It finds the game (or downloads it from Genesis' update servers), exports the game
  data, starts your own server on this PC, patches the game and starts it. Settings → Play with friends hosts a server
  for friends or joins someone else's. Its news are RO3Emu's GitHub releases, it updates itself from the latest
  release (`RO3Emu-win-x64.zip`: downloaded, size and checksum checked, swapped in on restart; your game, `data`,
  `logs` and settings stay), and after an update it shows that release's notes once. Closing it stops the server it
  started. `RO3Emu.exe` stays for the command line.

  **Web panel** on the title bar opens the [web panel](panel.md) of the server you play on in your browser:
  - your own server: `http://127.0.0.1:5381` (the panel port the launcher starts the server with, on the bind address
    of the server's own settings, `Ro3Emu:Panel:BindAddress` or `Ro3Emu:BindAddress`; none or every interface is
    reached on loopback). When the server is not running yet, the entry starts it first (without the game);
  - a server you joined: its panel on the joined host, next to its HTTP port (5381 next to 5380, 5391 next to 5390)
    or on 5381. The launcher asks it once after the join, with a 3 second timeout; when nothing answers the entry is
    dimmed and says the host has not shared its panel. A panel behind a proxy on another address is not found this
    way: the host gives that address to its players;
  - `panelUrl` in `launcher.json` (below) wins over both.

  `RO3Emu.exe panel` prints and opens the same address from the command line (`RO3Emu.exe panel <address>` for
  another server's).
- **Operator mode** (`"mode": "operator"`, or a config with a `repository`): a server's own launcher, below. It shows
  only what the operator configures; nothing of RO3Emu.

The rest of this page is about operator mode: how to host your own patch repository and publish to it.

## How it works

- `src/Ro3Emu.Patcher` is the patch engine: a content-addressed repository, FastCDC chunks, zstd, a staging folder plus
  a journal commit, and self-update by renaming the running exe. The download cache keeps compressed chunks and
  deletes each one after its last use, so a fresh install needs about the client's size in free space, not twice it.
- The repository is plain static files, served by any web server (nginx, Apache, a CDN bucket):
  - `channels/<channel>.json`: the only mutable file; it names the current client manifest and launcher for a channel
    (for example `test` or `live`);
  - `manifests/`: one immutable manifest per published version;
  - `chunks/`: the content-addressed, compressed data chunks;
  - `launcher/<version>/Ro3EmuLauncher.exe`: published launcher builds for self-update;
  - optional `news.json` for the launcher page, newest first: the first item is shown big, the next three in the
    "More news" list:
    `{ "items": [ { "tag", "color": "blue|green|orange|red|grey", "date": "2026-10-12", "title", "text", "url" } ] }`
    (`date`, `url` and `color` are optional; with a `url` the item gets "Read more" and opens in the browser).
    Without `news.json` the page shows only the server's name and its online status;
  - optional `channels/<channel>-notes.json`, the "What's new" sheet shown once after an update:
    `{ "versions": { "<version>": [ "Fixed …", "Added …" ] } }`. Upload it with the channel file. The launcher
    remembers the version installed at its first check, so a fresh install shows nothing and the next update shows the
    notes of the version it installed;
  - optional `hero.jpg`, the large picture beside the news (see `hero` below).
- The launcher pings `<server>/ro3emu/status` (player count only) for the online indicator.

## Configure the launcher

`src/Ro3Emu.Launcher/launcher.json` is built into the exe; it selects RO3Emu mode. Build your launcher with your own
file (`dotnet publish src/Ro3Emu.Launcher -c Release -p:LauncherConfigFile=<path to your launcher.json>`). A
`launcher.json` placed beside the exe overrides the built-in one, which is handy for testing against a local repository.

```json
{
  "mode": "operator",
  // Patch repository (static HTTP) and the channel this launcher follows.
  "repository": "https://patch.example.com/ro3/",
  "channel": "test",
  // The game server's HTTP base; the launcher pings it for the online indicator.
  "server": "http://127.0.0.1:5380",
  // Your server's name: title bar, built-in hero picture, tray icon, shortcuts.
  "serverName": "RO3Emu",
  // Optional: the hero picture. A file beside the exe ("hero.jpg") or a URL relative to the repository
  // ("art/hero-2026-10.jpg"; an absolute https URL works too). Leave it out to use <repository>/hero.jpg when it exists.
  "hero": null,
  // Optional: links on the title bar, opened in the player's browser. None by default.
  "links": [
    { "label": "Website", "url": "https://example.com/" },
    { "label": "Discord", "url": "https://discord.gg/example" }
  ],
  // Optional: your server's web panel (docs/guide/panel.md). The title bar's Web panel entry opens it; without it
  // there is no entry.
  "panelUrl": "https://panel.example.com/"
}
```

Set `repository` to the public URL of your repository folder and `server` to your game server's public HTTP address.

- `serverName` and `links` are all the branding there is: a fork for another server changes `launcher.json`, not the
  page. A link may set `"icon"` (`globe`, `chat`, `book`, `trophy`, `flag`, `code`, `link`); otherwise the page picks
  one from the label (Discord gets the chat bubble, a wiki or guide the book). Without `links` the title bar shows none.
- `panelUrl` is the public address of your [web panel](panel.md), for example `https://playnusky.com:8443` behind
  your https proxy (an `http` or `https` address; anything else is ignored). The title bar then shows **Web panel**
  first among the links, and it opens that address in the player's browser. An operator's launcher never looks for a
  panel by itself: without `panelUrl` there is no entry. In RO3Emu mode `panelUrl` also wins over the panel of the
  server the player plays on.
- The **hero** is the big framed picture. In order: `hero` from `launcher.json`, else `<repository>/hero.jpg`, else a
  built-in sky (no art needed). JPEG, PNG or WebP up to 8 MB; about
  1300 × 1050 px fills the frame on high-DPI screens, and the picture is cropped to the frame (centre, slightly above
  the middle). The launcher downloads it and serves it to the page itself, so an `http://` repository works, and keeps
  the last one in `%LOCALAPPDATA%\RO3Emu\hero.cache` for starts without the repository. Use art you have the rights
  to.

## What the player sees

- **Install / Update / Play** in the bottom bar, with the installed and available version, the status, and the
  progress (speed, time left, Pause; a paused download continues where it stopped).
- **Web panel** on the title bar (with `panelUrl`): the server's web panel in the browser.
- **Settings** (the gear): the game folder (change, open), Repair, Desktop / Start menu shortcuts and **Uninstall**. Uninstall is
  offered only for a folder the launcher installed into while it was empty or new; a folder that already held a client
  (for example the player's own Genesis ROO_PC) is never deleted.
- On the first run the launcher asks once whether to add Desktop / Start menu shortcuts to itself (it never moves or
  copies itself).
- **Play** hides the launcher to a tray icon; it comes back when the game closes and checks for updates again. Tray
  menu: Show launcher / Exit. The game does not need the launcher while it runs.
- Player choices live in `%APPDATA%\RO3Emu\launcher-settings-<repository>.json` (`clientDirectory`, `shortcutsAsked`,
  `notesSeenVersion`, `ownedDirectory`), one file per server's launcher, so two servers' launchers on one PC keep
  their own game folders. RO3Emu's own app uses `launcher-settings-ro3emu.json`. The first start of a launcher that
  used the old shared `launcher-settings.json` takes over its choices once.

## Releasing

All commands below use placeholders: `<client dir>` is a clean Genesis RO3 PC install (the folder with `ro3.exe`),
`<repo dir>` is your local copy of the repository folder, `<server url>` is your game server (for example
`http://127.0.0.1:5380` for a local test) and `<channel>` is the channel name.

### Client

Publish a new client version after a change to `Ro3Emu.ClientHooks` or the localization, or when the server address
or gate key changes:

```
Ro3Emu.ClientTools release <client dir> <server url> <gate_rsa_public.pem> <repo dir> <version> <channel>
```

The published file set is exactly the Genesis install (`.ro3-update/verified-baseline.txt`). The patched DLLs are
rebuilt from the originals in `ro3emu-backup` inside a staging copy, so the source client itself may already be
patched for another server. Only changed chunks are new, so a hooks change is a few MB.

### Launcher

Bump `<Version>` in `src/Ro3Emu.Launcher/Ro3Emu.Launcher.csproj` first, then:

```
dotnet publish src/Ro3Emu.Launcher -c Release -o <out>
Ro3Emu.ClientTools release-launcher <repo dir> <channel> <out>\Ro3EmuLauncher.exe <version>
```

### Upload

Copy `chunks/`, `manifests/` and `launcher/` to the web server first and `channels/` last, so a player never sees a
channel that points at files that are not there yet. Any method works (rsync, scp, tar over ssh, an object-storage
sync). Also put a copy of `Ro3EmuLauncher.exe` at the repository root (for example
`https://patch.example.com/ro3/Ro3EmuLauncher.exe`) as the download link for new players.

## Development

- `RO3EMU_LAUNCHER_UI=<path to src/Ro3Emu.Launcher/ui>` serves the launcher page from disk and enables the dev tools.
- `ui/index.html?mock=install|update|ready|downloading|repair|offline|error|checking|running` previews the page in a
  normal browser (serve the `ui` folder over HTTP); add `&panel=settings`, `&notes=1` (What's new), `&first=1` (the
  shortcuts question), `&hero=<image url>`, `&links=0`, `&news=0` (no `news.json`), `&webpanel=0` (no Web panel
  entry; with `?app=ro3emu` a joined server without one) or `&fast=10` (a faster fake download).
- A `launcher.json` beside the exe overrides the built-in one (for example a local repository such as
  `http://localhost:8790/`).
