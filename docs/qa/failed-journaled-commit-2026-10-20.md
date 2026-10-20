# Failed journaled commit no longer zeroes reads or strands admission — October 20, 2026

Found while designing the pressured new-source FlaUI case. Branch **codex/app-recovery** on **da62995**; isolated `IsolatedProfile` databases only.

**Defect (introduced with the journal, 5389c9a).** Before the journal, both player save paths caught their own failures. The journaled path returned `CommitDurableObservationAsync` directly, so a failed SQLite apply faulted the save task. Examples are a writer held past Microsoft.Data.Sqlite's 30 s command timeout, which the October 20 real-app log showed as `SQLite Error 5: 'database is locked'`, or any other SQLite error. Reads and deferred sources wait on those tasks:

- the next unit's resume read in the same player failed and returned **0**, so its next save could overwrite that unit's real position;
- a source change deferred behind the failed save never ran, and the player stayed in "Saving progress before continuing..." with disabled controls.

**Repair.** `SaveJournaledProgressAfterLoadAsync` now catches and logs like the direct paths. A staged position stays in the journal and replays before the next read. If staging itself failed, an ERROR says the position could not be journaled or saved. That pre-existing limit is unchanged.

**Regressions** use a real SQLite trigger that aborts updates of one unit:

- `AFailedJournaledCommitDoesNotHideTheNextUnitsSavedPosition` (catalog and legacy): the next unit restores its own 371 s. The failed 120 s stays journaled while SQLite holds 90, and replays to 120 once the trigger is dropped.
- `ADeferredSourceIsAdmittedAfterAnAcceptedCommitFails` (catalog and legacy): pressure behind a held writer defers the latest source. After release the first unit's commit fails, yet the source is admitted and restores 371. The failed position stays journaled and the adjacent row stays 57.

| Run | Result | TRX |
| --- | --- | --- |
| Before repair | **0 / 4**: zero restored ×2; admission faulted with `SQLite Error 19` ×2 | `failed-journaled-commit-before-20261020.trx` |
| After repair | **4 / 4** | `failed-journaled-commit-after-20261020.trx` |
| `ResumeDispatcherContentionTests` (related) | **44 / 44**, 37 s | `failed-journaled-commit-related-20261020.trx` |

No real-app run covers this repair yet; the pressured new-source FlaUI case (next) exercises the same admission path.
