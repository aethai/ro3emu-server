# ADR 0007: Signed Release Channels, Prebuilt Verification, and Export Sandbox

- **Status:** Accepted

## Context

External server operators and players download releases, patch channels, and prebuilt client DLLs directly from GitHub. In addition, `Ro3Emu.DataExport` executes Lua bytecode extracted from game client files on the operator's or player's machine. Without cryptographic verification and sandboxing, a tampered channel manifest, stale prebuilt DLL, or hostile Lua chunk could compromise hosts or players.

## Decision

1. Sign release channel manifests (`channels/*.json`) with RSA-3072 and verify signatures through a shared downloader used across the launcher, server updater, and patcher; reject untrusted or tampered channels.
2. Record SHA-256 hashes of `prebuilt/client` DLL inputs and outputs (`source.sha256`), verify them in CI (`client-stamp.ps1 -Check`), and protect `prebuilt/**`, `.github/**`, and `tools/release/**` with `CODEOWNERS`.
3. Restrict `.github/workflows/release.yml` to `main`/tags, pin third-party actions by commit SHA, publish `SHA256SUMS`, and generate GitHub build provenance attestations (`attest-build-provenance`).
4. Sandbox the Lua 5.4 VM in `Ro3Emu.DataExport` by stripping `os`, `io`, `package.loadlib`, and `dofile`, and restrict `UrlRewrite` to `http`/`https` schemes.

## Consequences

- Tampered channel manifests, mismatched prebuilt DLLs, and dangerous Lua primitives fail closed.
- Every published release artifact can be independently verified via `SHA256SUMS` and provenance attestation.
