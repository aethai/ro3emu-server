# Architecture

This document describes the server architecture, module layout, threading model, message pipeline, and persistence guarantees so a developer without the game client can build, run tests (`Ro3Emu.NoClient.slnf`), and navigate the codebase.

## 1. Solution and Module Map

The repository separates client-independent server/tooling projects from the Unity client injection DLLs (`Ro3Emu.ClientHooks`, `Ro3Emu.Loc`, `Ro3Emu.ClientTools`). Anyone without a game client copy can build and test everything in `Ro3Emu.NoClient.slnf`:

```bash
dotnet build Ro3Emu.NoClient.slnf
dotnet test tests/Ro3Emu.Tests -c Release --filter "Category!=Slow"
```

### Core Projects

| Project | Role |
|---|---|
| `src/Ro3Emu.Server` | HTTP endpoints, MicroGate, GameGate, single-threaded `GameLoop`, gameplay services, SQLite persistence, and economy ledger. |
| `src/Ro3Emu.Server.Generators` | Roslyn source generator emitting strongly-typed config views (`[ConfigTable]`, `[ConfigConstants]`) from exported client tables. |
| `src/Ro3Emu.Panel` | Built-in web control panel (player portal and admin console) hosted on its own port. |
| `src/Ro3Emu.ClientPatching` | Shared client binary/URL/Lua patching logic consumed by `Ro3Emu.Setup.Core` and `Ro3Emu.ClientTools`. |
| `src/Ro3Emu.Setup.Core` / `src/Ro3Emu.Setup` | `RO3Emu.exe`: client discovery, data export, server bootstrap, and client patching. |
| `src/Ro3Emu.DataExport` | Sandboxed Lua 5.4 VM and asset/navmesh/localization exporter for game client data. |
| `src/Ro3Emu.Patcher` / `src/Ro3Emu.Launcher` | Content-addressed client updater and WPF/WebView2 player launcher. |
| `src/Ro3Emu.Bot` / `Ro3Emu.BotCli` / `Ro3Emu.LoadTest` | Real-protocol scripted bot client and S1-S9 load scenarios. |

### Server Internal Layers (`src/Ro3Emu.Server`)

- `Data/`: Exported client config tables, typed generated views (`PublicParams`, `GuildSettings`, etc.), and startup schema validation. Zero dependencies on `Game`.
- `Net/`: Phonest framing, RC4 + native `ZLibStream` (`StreamConvertor`), TCP listeners (`GateListener`), and voice cipher. Zero dependencies on `Game`.
- `Maps/`: Scene editor spawns, navigation grids, and spatial helpers.
- `Accounts/` and `Accounts/Persistence/`: SQLite database lifecycle, versioned schema migrations (`DatabaseMigrations`), `DbWriter`, gameplay and panel `DbReadPool`s, `DbPlan` definitions, retention (`ReceiptRetention`), and online backups (`DatabaseBackup`).
- `Economy/`: Immutable ledger verification (`EconomyLedgerReader`, `EconomySnapshotReader`) and exactness checking across all asset compartments.
- `Http/`: SDK login/auth endpoints, operator endpoints (`/metrics`, `/ro3emu/gm`), gift codes, and web panel backends (`PanelBackend*.cs`, `PanelOperations*.cs`).
- `Micro/`: Pre-world microservice gate (`LoginService`, `ClusterRecommendService`, `NameService`, `PlatformProxyService`) routed via `MicroServiceRouter` with explicit `IMicroService.Admission`.
- `Composition/`: Feature-scoped DI registration and wiring modules (`CoreComposition`, `CharacterComposition`, `CombatComposition`, `ActivitiesComposition`, `GuildComposition`, `MarketComposition`, `DungeonComposition`, `TerritoryComposition`, `SocialComposition`, `PanelComposition`) called from `ServerHost.cs`.
- `Game/`: Gameplay services, organized into 9 domain folders while keeping `namespace Ro3Emu.Server.Game;` for persisted type-name compatibility:
  - `Core`: `GameLoop`, `GameCalendar`, `GameSession`, `SessionRegistry`, `SessionSlot`, `GameMessageRouter`, `RoleSystems`, `IRoleLifecycle`, `Ownership`, `Messaging`, `Persistence`.
  - `Character`: inventory, stats, equipment, cards, pets, mounts, totems, wardrobe, titles, life jobs, season progression.
  - `Combat`: damage calculation, skill casting, buffs, monster AI, `ICombatRule` implementations.
  - `Activities`: daily/weekly events, lottery, battle pass, tower, gathering, timed/casual battles, MVPs.
  - `Dungeon`: private instances, story dungeons, `IDungeonMode` implementations (`GuildChallenge`, `DraculaWedding`).
  - `Guild`: guilds, guild home, campfire, caravan, hunting, guild league.
  - `Market`: trade house, stalls, auction, mail, shops, `MarketStore`.
  - `Social`: chat, friends, teams, adventure groups, tavern, feedback, voice tickets.
  - `Territory`: territory wars, siege, faction world, war machines, escort.

## 2. Message Pipeline

1. **HTTP Auth (`/sdk/login`)**: Verifies or registers credentials (PBKDF2 off the loop), checks `account_ban`, and issues a durable SDK token.
2. **MicroGate (port 20000)**: Client connects over TCP, verifies the SDK ticket (`LoginService`), selects area/server, and receives an engine token (`MixedLoginToken`) plus GameGate endpoint.
3. **GameGate (port 33100)**: Establishes encrypted/compressed TCP session (`StreamConvertor` using RC4 + `ZLibStream`), authenticates via `GateAuthRegistry`, and creates a `GameSession`.
4. **GameLoop Dispatch**: Incoming frames are handed to `GameMessageHandler` on the single-threaded `GameLoop`.
5. **Route Admission and Typed Dispatch**: `GameMessageRouter` validates the route's declared `RouteAdmission` (`Any`, `Account`, `Role`, `InWorld`) before deserializing the protobuf payload and invoking the typed sync or async handler registered by an `IMessageModule`.
6. **Cross-System Hooks and Lifecycle**:
   - Role login, scene load, move, and logout run in deterministic order through `IRoleLifecycle` (pinned by `tests/Ro3Emu.Tests/Snapshots/`).
   - Cross-service events (kills, damage dealt, scene loaded, item changes) dispatch through `AsyncHook<T>` with explicit subscription ordering and error isolation (pinned by `tests/Ro3Emu.Tests/Snapshots/hook-order.txt`).
   - PvP and event combat overrides are composed through DI via `IEnumerable<ICombatRule>`.

## 3. Threading Model

- **GameLoop thread (1 dedicated thread)**: Owns all live in-memory gameplay state (`World`, `GameSession`, `MonsterUnit`, combat, quests, active instances). Time comes solely from `TimeProvider` and `GameCalendar`, allowing deterministic virtual-time tests via `GameLoop.Advance`. `GameLoop.AssertOnLoop()` guards loop-confined state.
- **Network I/O threads (async sockets)**: Perform framing, RC4 encryption, and zlib compression/decompression off the loop. Outbound sends copy or serialize messages before returning.
- **SQLite Writer thread (`ro3-db-writer`, 1 dedicated thread)**: Serializes all writes through `DbWriter` with bounded durable and lazy queues, group commit (`MaxGroupJobs = 64`, `MaxGroupExecution = 25 ms`), per-plan savepoints, and `PRAGMA synchronous = FULL` in WAL mode.
- **Gameplay Read Pool (`ro3-db-read-*`, 4 threads)**: Bounded `DbReadPool` executing `PRAGMA query_only = ON` snapshots for logins, role loads, and gameplay queries.
- **Panel Read Pool (`ro3-db-panel-*`, 2 threads)**: Isolated `DbReadPool` (`AccountStore.ReadPanelAsync`) serving web panel and anti-cheat analytics queries so heavy admin reads cannot starve gameplay reads.
- **Background services**: `DatabaseBackup`, `ReceiptRetention`, and `CheatMonitor` run off the game loop and submit bounded batches through `AccountStore`.

## 4. Persistence, Ownership, and Recovery

- **Versioned Migrations**: `DatabaseMigrations` manages schema upgrades transactionally using `PRAGMA user_version`. Startups on an up-to-date database run 0 DDL statements; opening a database from a newer schema version fails fast.
- **Startup Integrity and Writer Faults**: `AccountStore` checks for unfinished restores (`DatabaseRestore.ThrowIfUnfinished`) and runs `PRAGMA integrity_check(5)` on startup before opening the writer. Terminal storage errors during `COMMIT` (such as `SQLITE_FULL` or `SQLITE_IOERR`) transition `DbWriter` into terminal failure, reject new writes, and trigger clean host shutdown via `IHostApplicationLifetime.StopApplication()`.
- **Commit-Before-Reply and Ownership**: Gameplay mutations reserve affected roles and entities in `OwnerRegistry`, build a detached `DbPlan`, verify expected revisions (`owner_revision` and `role_lifecycle`), commit durably via `DbWriter`, and only then publish state to memory and reply to the client.
- **Single Economy Ledger Path**: Every economy-affecting plan appends its receipt (`operation_receipt`), natural source claim (`operation_source`), and asset movement legs (`economy_entry`) exclusively through `EconomyLedgerWrite.Append`.
- **Receipt Fingerprint Compatibility**: `EventPlanFingerprint` and `EconomyChildIntent` persist `DbPlan` type names and field names. `ReceiptFieldNamesTests` pins all persisted field names against `tests/Ro3Emu.Tests/Snapshots/receipt-field-names.txt`.
- **Retention and Backups**: `ReceiptRetention` compacts old receipt payloads, extracts compact `mail_delivery_replay` keys before compacting old `mail/*` receipts, and rolls old `economy_entry` rows into `economy_daily_aggregate`. `DatabaseBackup` performs online WAL-snapshot copies verified with `PRAGMA quick_check`, compresses older copies, and enforces count, free-space reserve, and total-size caps.

## 5. Architecture Decision Records

Key architectural decisions are documented in [`docs/decisions/`](decisions/):

- [0001: Single-Threaded Game Loop and Virtualizable Clock](decisions/0001-single-threaded-game-loop.md)
- [0002: SQLite WAL, Single Writer, and Isolated Read Pools](decisions/0002-sqlite-wal-single-writer-read-pools.md)
- [0003: Commit-Before-Reply, Ownership Reservations, and Economy Ledger](decisions/0003-commit-before-reply-and-economy-ledger.md)
- [0004: Generated Config Views and Downward-Only Code Quality Ratchets](decisions/0004-typed-config-views-and-roslyn-ratchets.md)
- [0005: Explicit Hooks, Role Lifecycle, Combat Rules, and Route Admission](decisions/0005-explicit-hooks-lifecycle-and-route-admission.md)
- [0006: Domain Folders with Preserved Namespaces and Layer Boundaries](decisions/0006-domain-folders-and-layer-boundaries.md)
- [0007: Signed Release Channels, Prebuilt Verification, and Export Sandbox](decisions/0007-supply-chain-and-signed-channels.md)
