# ADR 0002: SQLite WAL, Single Writer, and Isolated Read Pools

- **Status:** Accepted

## Context

RO3Emu targets single-realm self-hosted deployments where zero-dependency setup (one database file, easy backups/restores) is essential. Under concurrent login storms and active gameplay, ad-hoc SQLite connections suffered from write lock contention, high fsync latency per commit, and web panel analytics queries competing with gameplay reads. Furthermore, silent corruption or disk-full errors during `COMMIT` could leave the server running in an inconsistent state.

## Decision

1. Open `ro3emu.db` in WAL mode (`PRAGMA journal_mode=WAL`, `PRAGMA synchronous=FULL`) and enforce single-process ownership via `RealmLock` plus `DatabaseRestore.ThrowIfUnfinished`.
2. Funnel every runtime write through one dedicated `DbWriter` thread (`ro3-db-writer`) with bounded durable/lazy queues, savepoint-isolated group commit, and `DbWriteGuard` SQLite authorizer enforcement.
3. Serve reads through bounded read-only pools (`PRAGMA query_only=ON`): a 4-worker gameplay pool (`AccountStore.ReadAsync`) and an isolated 2-worker web panel pool (`AccountStore.ReadPanelAsync`).
4. Manage schema changes exclusively through transactional `PRAGMA user_version` migrations (`DatabaseMigrations`), verify database integrity at startup (`PRAGMA integrity_check(5)`), and treat fatal storage errors at `COMMIT` as terminal faults that trigger clean host shutdown (`IHostApplicationLifetime.StopApplication()`).

## Consequences

- Operations stay simple (single SQLite file, online WAL backups, verified restores) while sustaining high write throughput via group commit.
- Heavy admin/analytics queries in the web panel cannot starve gameplay login or market reads.
- Storage failures fail fast and cleanly rather than acknowledging uncommitted gameplay progress.
