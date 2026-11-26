# ADR 0005: Explicit Hooks, Role Lifecycle, Combat Rules, and Route Admission

- **Status:** Accepted

## Context

As gameplay features grew, cross-service wiring accumulated over 100 public `List<Func<...>>` hook lists, a 68-dependency `RoleSystems` coordinator, manual combat delegate wiring in `ServerHost.cs`, and message handlers that repeated boilerplate session/role checks and `#pragma warning disable CS1998` suppressions. Ordering bugs were easy to introduce silently.

## Decision

1. Replace raw hook lists with `AsyncHook<T>` supporting explicit `(handler, order, name)` subscriptions, per-subscriber error isolation, metrics, and a CI-pinned order snapshot (`tests/Ro3Emu.Tests/Snapshots/hook-order.txt`).
2. Compose combat mode overrides through DI (`IEnumerable<ICombatRule>`) instead of manual delegate assignment in `ServerHost.cs`.
3. Coordinate role login, scene load, movement, and logout through `IRoleLifecycle` implementations ordered explicitly and pinned by snapshot tests, keeping `RoleSystems` small and decoupled.
4. Require every game route on `GameMessageRouter` to declare its `RouteAdmission` (`Any`, `Account`, `Role`, `InWorld`) and use typed sync or async `Map<TReq>` overloads (eliminating numeric route literals and `#pragma CS1998`), and require every `IMicroService` to declare its admission policy.

## Consequences

- Adding or removing a feature no longer requires editing `ServerHost.cs` or central switch statements.
- Unintended changes to hook or lifecycle execution order fail deterministically in CI snapshot tests.
- Missing role/world admission checks on newly added routes are caught structurally at registration time.
