# Hosted checkpoint 07d0eca and the 20-player admission drain — October 21, 2026

[Run 39661155110](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/39661155110) for **07d0eca**: **1,417 passed / 1 failed / 11 skipped of 1,429** in 15 min 27 s. Because the .NET step failed, the Python step did not run, so the node-timeout repair from **d87c4e2** is still unconfirmed on a runner.

## The failure

`ResumeDispatcherContentionTests.SharedAdmissionBoundsUnitBurstsAcrossIndependentPlayersAndKeepsEveryAcceptedPosition` failed with "The current unit must receive its delayed resume within the deadline." At the deadline, 43 shared operations were still pending.

The case drives 20 mixed catalog and legacy players through 200 rounds of unit changes while a SQLite writer is held. It then releases the writer and waits for every player's deferred resume, allowing the default **5 s** for the first one. That first wait covers the whole serialized backlog of saves, ownership changes and reads, each with SQLite commits and journal writes. The runner's storage is slower than this machine's, so the backlog outlasted 5 s. This is the same storage-speed pattern as the two earlier runner failures ([admission QA](pressured-source-admission-2026-10-21.md), [close-path QA](close-path-journal-logs-and-player-database-lifetime-2026-10-20.md)). The app's behavior was correct.

## Repair (test only)

- `PumpUntil` and `WaitForResumeLoad` take an optional deadline; the default stays 5 s for every other case.
- This case's post-release drain allows **60 s**.
- `RecoveryLayoutTests.RunSta` takes an optional limit (default still 15 s); this case's whole body allows **120 s**, because it contains the drain.
- The shared profile cleanup in `IsolatedProfile.Dispose` (final flush and player-storage disposal) allows 60 s instead of 5 s. All of these are ceilings, not delays.

## Reproduction

A temporary scratchpad variant pulsed a SQLite write lock and the journal lock (150 ms held, 30 ms free) while the backlog drained. The test file was restored afterwards with a matching hash.

| Run | Result | TRX |
| --- | --- | --- |
| Journal lock only, old 5 s wait | Passed: journal speed alone does not explain it. The result file was not kept. | — |
| Slow SQLite and journal, old 5 s wait | **Fail**, with the runner's message ("must receive its delayed resume within the deadline"; 63 shared operations pending) | `shared-admission-slow-storage-before-20261021.trx` |
| Slow SQLite and journal, 60 s wait, before the cleanup change | The drain passed. Cleanup then timed out at 5 s, behind the emulation's own writer, which could block for 30 s. The emulation now uses a 1 s command timeout, and cleanup got the ceiling above. | (replaced) |
| Slow SQLite and journal, 60 s wait and cleanup ceiling, before the case limit | The drain and cleanup passed; the whole case then exceeded `RunSta`'s 15 s budget ("WPF layout did not finish"), so the case got its own limit. | (replaced) |
| Slow SQLite and journal, 60 s drain, 120 s case limit | **Pass**, 47 s | `shared-admission-slow-storage-after-20261021.trx` |
| Whole contention class, recovery layout tests and guard cases, normal storage | **55 / 55**, 50 s | `shared-admission-deadline-fix-20261021.trx` |

On normal storage this case still finishes in a few seconds; the ceilings only matter when storage is slow.
