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

## Active work and limits

The intended repair is bounded asynchronous application closing: capture final positions before native teardown, wait for accepted progress operations (including earlier closed players) without blocking the dispatcher, then dispose and exit. No such repair or passing close/restart result is claimed yet. Forced process exit, locks beyond the orderly-close deadline, bounded queues, broader profiles/package behavior and all-provider acceptance remain open. Books remain deferred; Arabic cartoons remain last among non-Books work.
