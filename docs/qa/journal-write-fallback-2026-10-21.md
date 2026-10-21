# Accepted positions are saved directly when the journal cannot take them — October 21, 2026

Follow-up to the [failed-commit repair](failed-journaled-commit-2026-10-20.md), which recorded this as an open limit. Branch **codex/app-recovery** on **025d96a**; isolated `IsolatedProfile` databases only.

**Gap.** An accepted position is staged in the journal before SQLite. If staging itself failed, the position was lost: it reached neither the journal nor SQLite. That happens when the journal's lock is held elsewhere past its 3-second wait (for example by another process) or the sidecar cannot be written. A9f0ce2 had only logged it, as "could not be journaled or saved".

**Repair.** Each save now has its direct path, the one a player without a journal uses (catalog session save or legacy player database), fixed when the save is queued.

- A journaled save whose staging failed now falls back to that direct save. The fallback is tracked as an unjournaled position, so close and session-end logs report "not journaled" while it runs.
- The direct save keeps the existing latest-write check, so a newer accepted position for the unit supersedes it.
- If staging succeeded and only SQLite failed, nothing changes: the position stays journaled and replays.

**Regression.** `AcceptedPositionIsSavedDirectlyWhenTheJournalCannotBeWritten` (catalog and legacy): after a committed 90 s, a real file handle holds the journal's lock past its wait while 120 s is accepted. SQLite must then hold 120 and the journal no record for the unit.

| Run | Result | TRX |
| --- | --- | --- |
| Without the fallback | **0 / 2**: SQLite stayed at **90**; the 120 s position was lost | `journal-write-fallback-before-20261021.trx` |
| The resume cohort with the fallback, including both new cases, the real-app close-past-deadline journey and four child-process crash cases | **220 / 220**, 3 min 31 s | `journal-write-fallback-cohort-20261021.trx` |

**Limits.** A direct save is only as durable as SQLite: a forced exit during it can still lose that one position, as before the journal. A volatile (memory) database still has no journal.
