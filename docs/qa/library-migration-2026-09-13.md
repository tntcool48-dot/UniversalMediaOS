# Audiovisual library migration

September 13, 2026. IP08 library storage and reconstruction checkpoint.

The library now reads the legacy JSON array and a version-2 envelope containing entries. Keys retain a frozen work key and namespaced provider IDs. Entries retain aliases, categories, favorites/status, the last-opened/progress summary and original legacy records. Catalog and My List use the same entry-to-media reconstruction helper, preserving keyless provider identities and persisted opaque keys instead of independently parsing only TMDB IDs. Explicit TMDB/IMDb projections remain available for transitional consumers.

Before conversion, the service validates a candidate snapshot and creates a byte-for-byte backup named `audiovisual-library.json.v1-<sha256>.bak`. Backup creation uses a temporary file and never overwrites an existing backup. The service writes the candidate to a unique sibling temporary file, then atomically replaces the destination. In-memory state is published only after commit. Failed conversion, update or removal preserves the original file and the last committed in-memory state; uncommitted changes emit no event. Cancellation before load cannot cache an empty library. Malformed JSON and unsupported future versions are surfaced as failures and cannot be overwritten by a subsequent update.

Legacy TMDB IDs gain a namespace only when explicit content form or an unambiguous Movie/Television category establishes it. Unknown-form cartoons and title-only entries receive deterministic opaque keys. Duplicate ambiguous rows remain independently accessible through their new work keys; ambiguous old-key lookup fails rather than selecting an arbitrary row.

Legacy Movie/Cartoon records with the same provider identity and content form converge. Favorites are unioned; status/display come from the newest update with ordinal alias ordering as the tie-breaker, and the latest opening determines the summary. Categories, aliases and full original JSON records are retained. No legacy progress is imported into SQLite in this batch. Conflicting provider IDs remain separate. Provider ID values and canonical dictionaries preserve case.

New identity enrichment can retain an existing frozen key through a shared nonconflicting provider ID and records the incoming key as an alias. Multiple already-canonical entries sharing an identity require explicit reconciliation and are not silently overwritten. This remaining collision case differs from the implemented legacy conversion.

Verification: **390 passed, 22 existing skips, zero failures**, with zero build warnings/errors. Eleven new cases cover byte-preserving backups, repeatable migration, equivalent category merging, ambiguous/conflicting records, case-sensitive IDs, identity enrichment, injected commit failures, unreadable/future versions and cancellation. Existing library workflow and catalog tests pass. Tests used isolated temporary profiles; real migrated-profile desktop acceptance remains open.

Remaining IP08/IP15 scope: reconciliation of multiple existing canonical keys, verified legacy SQLite imports, playback-to-library summary synchronization, ordered writes across separate service/player instances and real migration/rollback journeys. Old app versions cannot read the new envelope; rollback requires the preserved pre-conversion profile and may omit later progress.

Evidence: `.artifacts/implementation/results/library-migration-e2e.trx`, `.artifacts/implementation/library-migration-checkpoint.json`, and the pre-edit source copy under `.artifacts/implementation/library-migration-before/`.

```powershell
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/library-migration-build
```
