# ADR 0001: Single-Threaded Game Loop and Virtualizable Clock

- **Status:** Accepted

## Context

Gameplay interactions (combat, movement, buffs, quests, instances, guild events) touch shared world and session state at high frequency. Fine-grained locking across dozens of services introduces deadlock risk, subtle race conditions, and inconsistent ordering between ticks and client messages. In addition, direct calls to `DateTime.UtcNow` / `DateTime.Now` and real-time `Task.Delay` waits made calendar reset boundaries (including DST transitions) fragile and caused tests to sleep in wall-clock time.

## Decision

1. All mutable gameplay state is owned by a single-threaded `GameLoop`. Services outside the loop enqueue work onto the loop; loop-confined code may assert confinement with `GameLoop.AssertOnLoop()`.
2. All gameplay time reads flow through an injected `TimeProvider` and `GameCalendar` (single source of truth for daily/weekly reset windows and server-day math across time zones). Direct `DateTime.UtcNow`/`Now` calls and ad-hoc reset offsets in `Game/` are blocked by Roslyn tests in CI.
3. `GameLoop.NowMs` tracks monotonic elapsed loop time even when tests pin `TimeProvider.GetUtcNow()`, and `GameLoop.Advance(TimeSpan)` allows deterministic virtual-time stepping in tests.

## Consequences

- No per-entity mutex choreography is needed inside gameplay handlers, keeping tick latency predictable and easy to reason about.
- Blocking I/O (including synchronous SQLite waits or logging sinks) must stay off the game loop, monitored by `DbAccessMetrics.LoopWaits`.
- Time-dependent tests (daily resets, DST boundaries, boss schedules, gathering timers) run deterministically without real-time sleeps.
