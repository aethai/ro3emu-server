# ADR 0006: Domain Folders with Preserved Namespaces and Layer Boundaries

- **Status:** Accepted

## Context

Nearly 500 source files lived flat in `src/Ro3Emu.Server/Game/`, making navigation and ownership boundaries opaque. At the same time, persisted receipts and reflection-based fingerprints embed full CLR type names (including namespaces), so changing namespaces for existing gameplay types would invalidate stored receipts or require invasive migration logic.

## Decision

1. Organize all `src/Ro3Emu.Server/Game/` source files into 9 domain folders (`Activities`, `Character`, `Combat`, `Core`, `Dungeon`, `Guild`, `Market`, `Social`, `Territory`) using `git mv` so history is preserved, while keeping `namespace Ro3Emu.Server.Game;` unchanged on existing types.
2. Enforce zero loose `.cs` files in the `Game/` root and enforce upward-only layer dependency ceilings (`Data` and `Net` have 0 references to `Game`; `Maps`, `Accounts`, and `Economy` stay within downward-only ceilings) in `CodeShapeTests`.
3. Keep `GameSession` compact by grouping domain-specific per-session state into cohesive state containers and `SessionSlot<T>` typed slots rather than adding ad-hoc properties directly onto `GameSession`.

## Consequences

- Developers can navigate gameplay features by domain folder without breaking any persisted receipt or serialization format.
- Lower infrastructure layers (`Data`, `Net`, `Accounts`) remain cleanly decoupled from gameplay services.
