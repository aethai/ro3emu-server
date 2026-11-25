# ADR 0003: Commit-Before-Reply, Ownership Reservations, and Economy Ledger

- **Status:** Accepted

## Context

Item duplication, lost currency, and split-brain state happen when a server mutates in-memory player state or sends success replies before persistence succeeds, or when two async operations modify the same character or shared market record concurrently. At the same time, retaining full raw intent payloads and `economy_entry` rows indefinitely caused unbounded database growth on live realms.

## Decision

1. Economy and progression mutations reserve all participating roles and entities in `OwnerRegistry`, build a detached `DbPlan` with expected revision vectors (`owner_revision` and `role_lifecycle`), commit atomically in `DbWriter`, and publish in-memory state only after commit succeeds.
2. Every economy plan appends its receipt (`operation_receipt`), natural source claim (`operation_source`), and asset movement legs (`economy_entry`) through a single helper (`EconomyLedgerWrite.Append`), guarded by a test in `PersistenceRoutingTests`.
3. Persisted `DbPlan` type and field names are pinned by `ReceiptFieldNamesTests` (`Snapshots/receipt-field-names.txt`) so historical receipts remain valid across upgrades.
4. `ReceiptRetention` keeps database size bounded by compacting old receipt payloads, extracting compact replay keys into `mail_delivery_replay` before compacting old `mail/*` receipts, and rolling old `economy_entry` rows into `economy_daily_aggregate`. Every persisted table must be classified in `EconomySnapshotReader` and covered by an explicit retention policy.

## Consequences

- Crashes, lost acknowledgements, and concurrent requests cannot duplicate items or currency.
- Refactors must preserve persisted `DbPlan` field names and namespaces.
- Long-running realms stabilize in disk usage while keeping idempotency and daily economic telemetry intact.
