# Close-path journal logs and player database lifetime — October 20, 2026

This batch closes findings 3 and 4 of the [October 20 assessment](independent-assessment-and-durable-resume-2026-10-20.md). Branch **codex/app-recovery**, on top of **d6b4284**. Every check used temporary `IsolatedProfile` or AppFixture sandbox profiles; no real profile, library, download or secret was read or written.

## Close and session-end logs now report journaling (finding 4)

Before, both deadlines logged "still pending" even when the accepted position was already in the journal beside the database. The October 20 real-app run logged exactly that while its journal held 71.001 s.

A drain whose deadline elapses now reports **journaled** only when all three hold:

1. the journal flush started with the drain completed without a reported failure;
2. no journal write is still in flight and none has failed since (`DurableResumeJournal.IsSettled`);
3. no direct save is pending. A direct save is a player without a durable claim, for example a memory database, and its position exists only in memory until SQLite commits it. `PlaybackViewModel` now marks these saves when it tracks them (`TrackPendingPersistence(..., unjournaledPosition: true)`).

Messages (both stay WARNING, because SQLite was blocked past the deadline):

- `[Resume] Application close deadline elapsed; accepted playback progress is journaled and its database commit is still pending.`
- `[Resume] Application close deadline elapsed with playback progress still pending and not journaled.`
- The session-end pair reads the same, prefixed `Windows session-end deadline elapsed`.

`App.DrainPlaybackProgressForSessionEnding` keeps its three-argument bool overload: true means drained into SQLite, false means the deadline elapsed. A new overload adds `out bool journaled`, and `OnSessionEnding` uses it.

## Session end still waits for SQLite (consideration 1b, not done)

Returning from session end as soon as positions are journaled would break the intent of the existing session-end tests:

- `SessionEndingCapturesAndDrainsExactProgressWithoutPumpingTheDispatcher` holds a writer for 300 ms. It expects a drained result within the 3 s deadline and SQLite at 120 immediately after.
- `SessionEndingDrainsAcceptedProgressFromAnAlreadyRemovedPlayer` holds a writer for 2.5 s. It expects the barrier to wait through it and leave SQLite at 120.
- `SessionEndingDeadlineDoesNotAddPerPlayerCloseWaitOrCancelAcceptedProgress` expects false only after the deadline has actually been waited (≥ 75 ms for a 100 ms deadline).

There is also a product cost. Only player loads replay the journal, and the Movie/TV library summary ("last watched" unit and position) is written only after the SQLite apply. An early return would leave the summary stale until a player next opens that profile. The wait it would save is bounded by the existing 3-second cap and happens only when another writer holds SQLite at shutdown. The barrier therefore keeps waiting up to its deadline, and the log now says what is safe when it gives up.

## Closing a player tab no longer disposes the player's database (finding 3)

`MainViewModel.CreateScopedViewModel` resolves each player from its tab's DI scope. `DatabaseContext` was a container transient, so the scope tracked it and disposed it when the tab closed. That happened right after `PlaybackViewModel.Dispose`, even though `DisposeResumeResourcesAsync` intends to dispose it only after accepted saves finish. `App.ConfigureServices` now registers `PlaybackViewModel` with an explicit factory that constructs its `DatabaseContext`. The scope still disposes the player, but no longer tracks the context. Journaled commits use their own context and were never affected. The exposed paths were the direct-save fallback and legacy reads still queued at close.

Regression `ClosingAPlayerTabScopeKeepsItsDatabaseUntilAcceptedSavesFinish` resolves the player through `App.ConfigureServices()`. It holds a real writer, disposes the tab scope, and asserts that the player's context is still usable while the save waits. After release, the position persists at 120 and the context is then disposed.

- Before the repair: **0 / 1**, failing at that assertion (`tab-scope-database-before-20261020.trx`). Its first attempt failed only in profile cleanup, because an in-flight commit still had `media_os.db` open and hid the real assertion. The test now drains its work in `finally`.
- After the repair: passes within the class run below.

## Harness finding: FlaUI's `Application.Close` can miss the window

Two runs of the real-app durability test failed before reaching the new assertion: the app never logged a close and did not exit within 40 s. Diagnostics showed that `Process.MainWindowHandle`, which FlaUI's `Close` posts WM_CLOSE to, had resolved to **0xC000C2E**: style `0x96000000` (a visible popup), unowned, untitled. The WPF main window was **0xDAD0B84**. In the passing diagnostic run, the same lookup returned the main window. The popup's style matches a WPF tooltip shown under the physical cursor on the second monitor, so it depends on where the cursor rests. `Process.CloseMainWindow` picks the first visible unowned top-level window in Z-order.

The durability test now closes the WPF window through its own UIA WindowPattern, which is the request the title-bar button makes. **Not changed:** `AppFixture.Restart()` and `Dispose()` still call `Application.Close()`, and with its default kill fallback a missed close becomes a silent kill instead of a normal close. Five other UI tests call `Restart()`. This was a follow-up for the shared fixture, since done: [fixture close QA](appfixture-window-close-2026-10-21.md).

## Verification

| Scope | Result | TRX |
| --- | --- | --- |
| Tab-scope regression before the repair | 0 / 1 (expected failure) | `tab-scope-database-before-20261020.trx` |
| `ResumeDispatcherContentionTests`: 34 existing cases (session end, close flush, deferred disposal, admission) and 6 new | **40 / 40**, 36 s | `close-path-journal-logs-20261020.trx` |
| Real app, second monitor, AppFixture sandbox: committed **36.801 s**, writer held, journaled **70.501 s**, close **17.965 s** with the new journaled log line and no "not journaled" line, restart restored 70.501 s, seeded row stayed 120.5 | **1 / 1**, 51 s | `packaged-resume-journaled-log-20261020.trx` |
| Diagnostic real-app run (window enumeration plus window-pattern close) | 1 / 1, 52 s | `packaged-close-diagnostic-20261020.trx` |

The new cases are: journaled behind a writer (catalog and legacy), where the journal file holds 120 and a private WAL reader still sees 90 when the drain reports journaled; a held journal lock reports not journaled (catalog and legacy); a direct save on a volatile database reports not journaled and still saves 120 after release; and the DI tab-scope case. Build: zero warnings or errors. No broad local suite ran; the hosted checkpoint remains the full check.

## Hosted runner timing (October 21)

The hosted checkpoint for **e632671** ([run 39620398565](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/39620398565): **1,406 passed / 1 failed / 19 skipped of 1,426**, 15 min 3 s) failed only `SessionEndingDeadlineReportsJournaledProgressBehindALockedWriter(catalog: False)`, at `Assert.True(journaled)`.

The case gave the drain **100 ms**, and the journal write had to land inside that window while the held writer blocked SQLite. On the runner's write-through storage a journal write took longer, so the drain correctly reported "not journaled" at its deadline. The pressured-admission case failed the previous checkpoint for the same storage-speed reason and passed on this one ([admission QA](pressured-source-admission-2026-10-21.md)).

**Repair (test only).** The case now uses the session-end handler's own **3 s** deadline. The held writer (5 s) still outlasts it, so the drain still times out with SQLite blocked.

A temporary scratchpad variant held the journal lock in 150 ms pulses during the drain, so each journal write took longer than 100 ms. The test file was restored afterwards with a matching hash.

| Run | Result | TRX |
| --- | --- | --- |
| 100 ms deadline, pulsed slow journal | **Fail in both modes** at `Assert.True(journaled)`, as on the runner | `session-end-journaled-slow-before-20261021.trx` |
| 3 s deadline, pulsed slow journal | **2 / 2**, 7 s | `session-end-journaled-slow-after-20261021.trx` |
| 3 s deadline, all session-end cases, normal | **11 / 11**, 11 s | `session-end-deadline-fix-20261021.trx` |

The other two 100 ms cases expect "not journaled" (a held journal lock; a direct save) and do not depend on storage speed.

## Limits

No real Windows session end was exercised; that needs the user (checklist group "pressured source admission, real Windows shutdown"). The session-end message pair is covered through the drain's result; the `OnSessionEnding` text itself is a direct conditional. An accepted observation whose journal write fails is still lost, because the commit waits on the failed receipt instead of falling back to a direct save. This existed before this batch and is now at least logged as "not journaled".
