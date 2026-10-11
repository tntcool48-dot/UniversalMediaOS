# Windows session-ending progress barrier — October 11, 2026

## Problem and repair

Normal window close already has a 15-second asynchronous progress drain. `App` had no `OnSessionEnding` override. WPF's synchronous session-ending event proceeds to application shutdown when it is not canceled, so that normal window-close drain was not a reliable barrier for logoff/shutdown. This is a code/lifecycle finding, not a reproduced physical Windows shutdown. [WPF session-ending contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.sessionending?view=windowsdesktop-9.0).

The new handler respects an existing cancellation, captures current players' positions on the UI thread, and synchronously waits for worker persistence for up to three seconds before returning. The shared progress service includes accepted work from already removed tabs. It reports success, an expired wait, or failure through the existing log. It does not set cancellation to veto Windows shutdown.

Session-ending capture freezes further observations before teardown. Dispose therefore does not enqueue another final save or add its normal two-second per-player save wait. Pending snapshots keep their existing keys, ordering and deferred database disposal. Normal app/tab-close behavior is unchanged.

## Verification

The reused Release build passed with zero warnings/errors. **13 unique affected/related checks passed, zero failures/skips, in 17 seconds**: six new session-ending cases and seven existing close/flush cases. No broad local suite or unchanged passing cohort was repeated. [Original focused TRX](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/session-ending-progress-focused-2026-10-11.trx).

- Actual SQLite writer locks exercised canonical catalog and legacy paths. Synchronous capture stayed on the calling STA thread; the wait used no dispatcher pump. Drains took **309.101 ms catalog / 303.962 ms legacy**, durably retaining **120 seconds** and the adjacent unit's **57 seconds** before teardown.
- A deliberately short **100-ms** wait expired while the writer remained locked. Player disposal then took **1.800 ms catalog / 1.788 ms legacy**, retaining its database until accepted persistence finished. After release, both paths durably retained 120 and the adjacent 57. This proves survival of a wait deadline while the test process remains alive.
- An already removed player's real accepted save participated in the barrier. An app without playback returned immediately. Existing normal-close/deferred-disposal/completion and app-wide flush checks stayed passing.

Tested source is `6379e86` plus this four-file production/test batch. WPF DLL SHA-256 **`BA93A4A4B8B21B3CC72BAB27063856E3DD1C2E1C6A359C20604CCF12CA90C2EB`**; Core DLL **`242B4C1D0550A523B40E20851141481F61283EF5ABAE66DB156D6B0306D1900C`**. These identify the focused local artifact, not a new published package. The actual open private QA app/package remains `e036323`; it was not replaced or claimed as this repair.

## Remaining limits

No user logoff, shutdown, reboot, OS end-session message, or forced process termination was performed. The real Windows lifecycle and the three-second production deadline remain physically unqualified; a deadline cannot guarantee a later write after the OS terminates the process. Locks beyond either exit deadline, global queues across distinct accepted source/ownership intervals, portable configuration migration and final package acceptance remain open. The existing isolated profile and six unrelated untracked paths were preserved; controlled fixtures used their own temporary profiles. The grouped persistence gate stays open.
