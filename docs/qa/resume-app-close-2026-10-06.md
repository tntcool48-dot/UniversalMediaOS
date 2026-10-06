# Pending resume at actual app exit — October 6, 2026

The previous contention tests kept their process alive after tab/player disposal, allowing accepted worker writes to finish. They did not establish durable progress through actual process exit.

The second-monitor probe used the existing isolated download-queue profile and the retained Pilot file, with services auto-management disabled and Arabic controls. The human's Debug process was left running. Only owned Release process **62608** was closed.

## Reproduced failure

1. Downloads opened Pilot at its isolated saved position. K paused native video and persisted **778.092 seconds** before the lock.
2. An owned Python helper held `BEGIN IMMEDIATE` on this isolated SQLite database. It made no updates, rolled back on release and had a 60-second safety deadline.
3. K resumed Pilot while progress writes waited. K then paused decoded video at **787.636 seconds**, verified by the actual native slider and paused screenshot.
4. Alt+F4 requested app close at **09:22:28.297 UTC**. The process exited while the writer still held its lock. A read-only query after confirmed exit still held **778.092 seconds**, losing **9.544 seconds** of accepted progress.
5. The writer released at **09:22:45.707 UTC**, after **39.625 seconds**. The adjacent S01E02/S02E01/Dune positions remained **339.236 / 371.022 / 318.291 seconds**. The original profile, six permanent videos, metadata/captions and configuration were not written.

Private launch/exit/lock metadata, rows and the paused-frame screenshot remain in `.artifacts/implementation/resume-app-close-20261006/`. This is a failed process-exit check on the production behavior published as `b330d45`; the original tab-close tests remain valid for a process that stays alive.

## Repair and verification

Application closing now pauses current players, captures their final positions and awaits accepted persistence before native teardown. The shared progress boundary also tracks reads/writes/ownership operations from players already removed from the UI. Closing waits asynchronously for up to **15 seconds**, disables further main-window interaction and posts the final Close after the first Closing event has returned, including the already-drained case. A timeout logs pending progress and permits exit; it does not cancel the accepted worker operations. Operations do not read native handles after teardown.

- **66 focused checks passed**, zero failures/skips, in **40 seconds**, build zero warnings/errors. New checks hold an actual writer beyond a removed player's own two-second close wait, then require app-wide flush to await its accepted position and preserve an adjacent row, for catalog and legacy paths. Another check requires an expired wait to leave accepted persistence alive. Existing source-generation, ownership, exact-unit, completion, native decode/replay and resume checks remain. An initial compilation warning about the posted dispatcher operation was fixed before this passing build. [Focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/resume-app-close-focused-2026-10-06.trx).
- Actual second-monitor Release process **41756** reopened Pilot from **778.092 seconds** and paused/persisted **789.274** before a new writer lock. It then played and paused at **799.952 seconds** under the lock. Alt+F4 at **09:38:22.942 UTC** retained the app beyond a **3.5-second process wait** while the paused video stayed rendered. The writer released at **09:38:27.242 UTC**, after **33.094 seconds** total. The app logged that accepted progress drained and exited by **09:38:27.684 UTC**. A read-only query held **exactly 799.952 seconds**; S01E02/S02E01/Dune remained **339.236 / 371.022 / 318.291**.
- A different Release process **67376** started at **09:38:57 UTC**. Downloads → Pilot loaded/restored **799.952 seconds** (rounded 800.0 in the log), decoded native video with an English cue, and K paused its visible slider at **810.295 seconds**. Normal close completed; the authoritative final row was **810.545**, with the same adjacent rows unchanged. This is near-position pause retention, not frame-exact timing. All owned UI processes were closed; the human's Debug process **40880** remained running.
- An additional second-monitor startup with **no playback tab** closed normally at **09:41:48 UTC**, verifying the already-drained asynchronous close path without reentering the initial Closing event. Owned process **77308** exited; the human's app remained open.
- Protected originals verified at **09:39:31 UTC**: six permanent videos retained size/mtime, metadata/caption hashes and configuration hash; original Pilot/S01E02/S02E01/Dune rows remained **803.205 / 132.484 / 371.022 / 318.291 seconds**. No Scale, permanent payload, partial download/cache, original configuration or account data was changed. A rejected geometry-free desktop click was corrected by fresh observation and is not counted as acceptance evidence.
- Physical build WPF SHA-256 **`85F9CA9FB7B1E9677EDDDBDA806004E9D938570DEB9F470F57C1A574A40F00EF`**; Core **`444B323F8A66C1DDD60616FE9C69FA89BCCF24934DECED7E1DE5A69EF5A2CB39`**. This identifies the locally tested close repair built before publication, on source HEAD `3d7086b` plus this uncommitted production change. Existing Release output locations were reused. Small private evidence remains in the directory above.

## Remaining limits

This repairs the demonstrated orderly process-close loss within the drain deadline. Forced process exit, locks beyond the **15-second** wait, bounded/coalesced queues, Windows shutdown/session-ending behavior, broader profiles/package behavior and all-provider acceptance remain open. The controlled expired-wait case does not establish durable progress after forcibly terminating the process. Books remain deferred; Arabic cartoons remain last among non-Books work.
