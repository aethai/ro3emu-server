# Who has GM (`Ro3Emu:GmAccounts`)

GM commands are the `!` lines in chat (`!help`), the client's GM panel (30300) and the GM transmit/inspect messages
(1031/1034). One check decides all of them: `GmCommands.Allowed`.

| `GmAccounts` | `GmOpenWhenEmpty` | server | in game | panel Admin |
|---|---|---|---|---|
| listed accounts | any | any | only those accounts, spelled as stored (an account name is one account in any letter case; the list matches its stored spelling) | the same accounts |
| empty | unset | local only, not started as Production | everyone (tests, `tools/run-server.ps1`, local play) | everyone |
| empty | unset | reachable from other machines | **nobody** | **nobody** |
| empty | unset | started as **Production** | **nobody** | **nobody** |
| empty | `true` | local only | everyone | everyone |
| empty | `true` | reachable from other machines | everyone (an explicit choice, logged loudly) | **nobody** |
| empty | `false` | any | nobody | nobody |

"Local only" means only this machine reaches the server: the game ports listen on loopback (`PublicHost` is
`127.0.0.1` and `BindAddress` is empty or loopback), the [web panel](panel.md) listens on loopback too, and no proxy
serves it (`Ro3Emu:Panel:PublicUrl` and `Ro3Emu:Panel:TrustedProxies` are unset). A bind address that is not an IP
address counts as reachable, so a typo never opens GM. Everything else is "reachable from other machines": a LAN or
public address, `0.0.0.0`, or a panel behind a reverse proxy.

"Started as Production" means the environment was set explicitly: `ASPNETCORE_ENVIRONMENT=Production`,
`DOTNET_ENVIRONMENT=Production` or `--environment Production`. ASP.NET's own default when nothing is set is also
called Production, but it does not count, so tests and local runs keep "empty = everyone".

`RO3Emu.exe` follows the same table without settings of its own: local play starts a local, Development server
(everyone is GM), and hosting for friends starts a Production server on the LAN or public address with the GM accounts
you give it.

At startup the server logs who has GM. With an empty list it warns loudly in both cases that matter:

- Nobody has GM: `!!! Ro3Emu:GmAccounts is empty, so NO account has GM commands in game (environment ..., reachable from other machines on ...)`.
- GM open to everyone on a server other machines reach, or in Production (only with `GmOpenWhenEmpty=true`): `!!! Ro3Emu:GmAccounts is empty and GM is open: EVERY account can use GM commands in game ...`

The [web panel](panel.md)'s admin area gives Admin to the listed accounts. With an empty list it gives Admin to every
account only on a local panel with GM open, never on a panel that other machines reach: there anyone who signs up
would own the server. The startup log says who the configured Admins are (`Web panel Admins: ...`), and warns when
there is none. Its GM console runs a command for any character in the game: the admin's rights count there, not those
of the character's account.

## Configuration

Like every `Ro3Emu:` option, the list can be set in `appsettings.json`:

```json
{
  "Ro3Emu": {
    "GmAccounts": [ "your-admin-login" ]
  }
}
```

or through environment variables, one per account: `Ro3Emu__GmAccounts__0=your-admin-login`,
`Ro3Emu__GmAccounts__1=another-login`, and so on. The value is the game login (account name), not a character name.

## The loopback tool

`POST http://127.0.0.1:5380/ro3emu/gm?account=<account>` (body `!warp 10111 150 190`) answers only on loopback and
keeps its own rule: with an empty list it runs for any online account, with a list only for listed accounts.
Production does not close it; it is the operator's tool on the server machine.

## A public server

On a server that other people can reach, start the host as Production and list the GM accounts. With systemd, a
drop-in such as `/etc/systemd/system/ro3emu.service.d/gm.conf` (adjust the unit name to yours) needs both lines:

```ini
[Service]
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Ro3Emu__GmAccounts__0=your-admin-login
```

More GMs: `Ro3Emu__GmAccounts__1=...`. Passwords never go in the unit, the repository or these docs. After
`systemctl daemon-reload` and a restart, the log shows `GM accounts: your-admin-login`.
