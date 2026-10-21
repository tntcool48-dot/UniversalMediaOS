# Rollback compatibility, and a stale journal record after a downgrade — October 21, 2026

Part of the release gate's "migration/backup/rollback" smoke tests. Branch **codex/app-recovery** on **dcc20cd**. Only AppFixture sandbox and temporary profiles were used.

## Clean rollback and upgrade pass

The reused publish folder still held the previous packaged candidate, **33d5bd4** (Release, framework-dependent). All 1,365 of its files matched their recorded SHA-256 values, apart from its later-edited identity text.

A temporary FlaUI probe (removed afterwards; `rollback-33d5bd4-probe-20261021.trx`) used one sandbox profile on the second monitor. Each step opened a generated library film through the real Player, skipped forward, paused and closed normally:

| Step | Restored | Saved |
| --- | --- | --- |
| Current build (Debug, this source) | — | **31.700 s**; the profile gained the `ResumeJournalRevisions` table |
| Older **33d5bd4** package, same profile | **31.7 s** ("Restored 'Rollback film' to 31.7s") | **62.201 s** |
| Current build again | **62.2 s**, the older package's save | **93.001 s** |

There were exactly two rows (film and seeded fixture, which stayed 120.5) and no critical log lines. The older build ignores the journal sidecar and the extra table.

## Hazard found: a pending journal record could overwrite later progress from an older build

**Scenario:**

1. The current build exits uncleanly with an accepted position journaled but not yet committed, for example behind a held writer.
2. The user runs an older build without the journal, which saves later progress directly.
3. The current build's next read replays the pending record.

That record's revision is newer than anything the journal had applied, so it overwrote the later save. A temporary service-level probe read **62.0 s** after the older build had saved **95.0 s**.

**Repair:**

- Each journal record now carries the row value its session last read for that unit (`BasePosition`). Reads go through `ReadOpen` for catalog players and the player's legacy read for legacy keys.
- The revision table records the position the journal last applied (`AppliedPosition`). Tables created by the first journal build gain the column in place.
- On replay, a pending record is skipped and acknowledged only if the current row matches neither its base nor the journal's last applied position. That means a writer outside the journal changed the row afterwards. The app logs "Kept a later position … saved outside the journal".
- Records written by the first journal build carry no base and replay as before.

## Verification

| Run | Result | TRX |
| --- | --- | --- |
| Four new invariants with the base ignored (the old behaviour) | **3 / 4**: the rollback case read **62** instead of **95**; the other three pass both ways | `journal-rollback-base-before-20261021.trx` |
| Journal invariants with the repair | **13 / 13** (9 existing, 4 new) | `journal-rollback-base-after-20261021.trx` |
| The October 20 resume cohort with this change, including the real-app close-past-deadline journey and four real child-process crash cases | **218 / 218**, 3 min 23 s | `journal-rollback-base-cohort-20261021.trx` |

The new invariants:

- the rollback case (pending 62 yields to the outside 95; the neighbour row is unchanged; the journal empties);
- a pending record still replays over the row its own session read;
- a pending record still replays over its own session's earlier commit, even when its base is stale;
- a revision table from the first journal build gains `AppliedPosition` and records 120.

## Limits

- Two app instances at once remain unsupported (no single-instance guard). With them, the order of an outside write and a pending record can be ambiguous.
- A row that was absent when read counts as base 0.
- The packaged smoke test of a new candidate built from this source follows separately.
