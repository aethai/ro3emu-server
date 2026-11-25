# ADR 0004: Generated Config Views and Downward-Only Code Quality Ratchets

- **Status:** Accepted

## Context

Game rules rely on hundreds of JSON config tables exported from the client. Ad-hoc string lookups (`Table("...")`, raw field names, magic numeric IDs for functions, stats, and message routes) allowed typos to slip past compilation and made refactoring error-prone. At the same time, legacy code already contained oversized classes, long methods, and over 1,400 analyzer warnings, so enabling strict rules overnight was impractical.

## Decision

1. Use the `Ro3Emu.Server.Generators` Roslyn source generator (`[ConfigTable]` and `[ConfigConstants]`) to emit strongly-typed views over exported config tables, validated at startup and in tests against the real export.
2. Enforce downward-only code-shape ratchets in `tests/Ro3Emu.Tests/CodeShapeTests.cs` (method length, constructor dependency counts, class size, line length, `Role!.`, numeric route/stat/function literals, clock usage, `GameSession` property count, linear session scans, and layer boundaries). New code cannot raise any count; PRs that lower a count must lower the constant in the same PR.
3. Track remaining Roslyn analyzer violations in `.editorconfig` and verify exact counts in CI via `tools/AnalyzerBaseline` so analyzer debt only moves downward.

## Consequences

- Quality regressions are blocked automatically in CI without stalling feature or refactoring work behind a giant one-shot cleanup.
- Contributors get immediate, actionable feedback when introducing raw config strings, oversized constructors, or new analyzer violations.
