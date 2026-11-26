# Night look (optional)

*Not the original game.* The client has no day and night: `MapConfig._iWeather` is 0 on every map of the export, and
the client has no weather, sky or time-of-day module. Each map carries fixed, pre-baked light that cannot be changed at
run time. The emulator can therefore offer a night look of its own, **off by default**, for an operator who wants it.

## Switching it on

`Ro3Emu__NightCycle=true` (`Ro3Emu:NightCycle`, [hosting](../guide/hosting.md)). Without it the server sends nothing and
the client never draws the layer.

## What the player sees

The client hook (`src/Ro3Emu.ClientHooks/NightCycle.cs`) draws a see-through dark layer over the finished world picture,
in a canvas of the world camera, so the menus and the HUD stay bright. It follows the player's own PC clock; nothing is
sent between player and server about time, and other players are not affected:

| PC clock | Look |
|---|---|
| 06:00-17:00 | day, no layer |
| 17:00-19:00 | dusk: fades in, warm orange first, then blue |
| 19:00-05:00 | night: the full layer (55% dark blue) |
| 05:00-06:00 | dawn: fades out |

The layer never shows on the character creation or login scenes.

## Towns and fields only

`NightCycleService` checks every logged-in player once a second and sends the client command `time place outdoor` or
`time place indoor` (message 77012, the one of the GM's `!client`) when it changes, and `time on` once per session.
A place is outdoor when the player is outside any instance on a map whose `MapConfig._iSceneType` is 0 (main city) or 1
(outside) (`enum_SceneType`, `Lua_GameDefine`). Everything else is indoor and never gets the layer, whatever the clock
says: instances (dungeons, raids, story scenes), dungeon floors and caves (6), guild war, the training ground, territory
war (8) and a map without a MapConfig row. *(inferred: the original game has no night; this rule is the emulator's.)*
Pharos Lighthouse (10537) has scene type 0 like the towns, so it gets the night look.

## GM commands

`!day`, `!dusk`, `!night`, `!dawn` and `!time <auto|day|dusk|night|dawn|hour 0-24>` set the look on the GM's own screen
only, whether or not the option is on (`!time auto` returns to the clock). The setting lasts until the game is closed.

## Tests

`NightCycleTests`: the option is off by default, the town and field rule, the command text. The client hook is not
built in CI (it compiles against a local client); `prebuilt/client` carries its build.
