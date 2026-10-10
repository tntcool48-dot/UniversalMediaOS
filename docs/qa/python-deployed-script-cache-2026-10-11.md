# Deployed Python readiness cache — October 11, 2026

After successful preparation, modifying a deployed scraper still reused Ready because the cache checked source hashes and only the existence of deployed files. Three new cases damaged each owned deployed script independently; all three failed before repair.

Cached readiness now requires identical source and deployed hashes for all three scripts. A change enters the existing copy/import/syntax preparation path. The first implementation exposed an uncaught sharing violation when hashing a locked deployed file; the new real Windows file-lock case failed before correction. Unreadable revision bytes now invalidate the cache, allowing preparation to report its structured `preparation_failed` state and recover after the lock releases. Healthy shared preparation, canceled-waiter isolation, existing deadlines and retry behavior remain intact.

The final scopes contain **16 unique affected/related passes**, zero final failures/skips, with clean incremental Release builds. The 15-case preparation scope excludes the two unchanged real-process pipe/cancellation cases; after the lock correction only five affected/shared-cache cases reran, four overlapping. These are fake-runner preparation fixtures plus a real OS file lock, not packaged Python/import repair acceptance. No full suite, global package installation or original profile mutation ran.

- [Initial three failures](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/python-deployed-script-before-20261011.trx), [15-case scope](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/python-deployed-script-after-20261011.trx).
- [Lock failure](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/python-script-lock-before-20261011.trx), [final five](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/python-script-lock-after-20261011.trx).

This repair validates deployed script bytes at preparation requests. Same-session runtime/package changes, CLI compatibility and broader physical service Repair remain open. The a3c5394 process-restart sample excludes this change.
