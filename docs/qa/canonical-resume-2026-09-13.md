# Audiovisual resume key adoption

September 13, 2026. Partial IP08 implementation.

Playback now uses the captured audiovisual WorkKey and UnitKey for authoritative SQLite resume reads and writes. This applies to native and embedded playback through the common resume configuration path. Feature positions survive source URL and display-title changes. Series positions distinguish seasons and episodes; works with separate provider IDs remain separate even when their titles match.

When an audiovisual context has an unresolved unit key, resume is disabled for that selection instead of falling back to a title or signed-URL hash. Calls without audiovisual context keep the existing anime/legacy key behavior. No SQLite schema change, row deletion or guessed import is performed. Existing title/URL rows remain intact, but are not automatically associated with a provider identity.

Each player now assigns a sequence to accepted resume writes per work/unit. Both synchronous and asynchronous writers check that sequence while holding the database semaphore. An older pending write cannot overwrite a newer completed position. Progress arriving after completion is ignored by the save queue. This orders writes within a player; coordination between separate player instances and full shutdown persistence guarantees remain IP15 acceptance.

Six added regression cases use isolated SQLite profiles and cover source/title/player changes, two seasons of the same show, a different same-title work, unresolved-unit preservation of a legacy row, and deterministically reordered writes after completion. Existing anime resume tests remain active.

Remaining IP08 work: versioned audiovisual-library JSON migration, verified alias import and collision reconciliation, shared catalog/My List reconstruction, stable persisted keys for identity-free cards, and library/last-unit summary updates from playback. SQLite is authoritative for this batch; the legacy JSON summary is not migrated or reconciled here. Live playback journeys also remain unverified.

Reproduce:

```powershell
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/canonical-resume-build
```

Final exact-build verification: 379 passed, 22 existing skips, zero failures; zero build warnings/errors. Results and source/build hashes are retained in the implementation artifacts.
