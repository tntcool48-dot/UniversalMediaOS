# Independent assessment and durable resume journal — October 20, 2026

This batch picks up the October 20 handoff. It records an independent review of the current worktree, the state of the uncommitted resume-durability work, the repairs made to it, and a prioritized plan. Branch **codex/app-recovery**, HEAD **a926d41**; the worktree matched the handoff snapshot. Nothing was committed, reset, cleaned or stashed. No real profile, library, download or secret was read or written; every check used temporary or AppFixture sandbox profiles.

## How the review was done

The code was read, not the history. The resume and playback-progress path was traced end to end: PlaybackViewModel queueing, PlaybackProgressService ordering, SQLite, the new journal, app/session shutdown and tab disposal. Tab and DI lifetimes, the native-first routing rule, metadata-provider selection, the loopback HLS proxy, the Watch Together relay, OAuth token storage, log sanitizing, dependency downloads, Python preparation and the CI workflow were spot-checked against their source. The large matching modules (scraper.py, audiovisual_scraper.py, DubAvailabilityService), torrent ownership and Books were not re-reviewed line by line; their open gates stay as the checklist records them.

## The uncommitted journal as handed over

It compiled cleanly on its first build (zero warnings/errors), but it was not working:

- All **9** new tests failed (`resume-journal-first-run-20261020.trx`). Five invariant cases never created their profile folder (`unable to open database file`). Four crash cases failed in cleanup with `media_os.db` still open, which hid their real outcome.
- **51 of 161** existing resume tests across 12 classes failed (`resume-related-inprogress-20261020.trx`), every one with the same `IOException: media_os.db is being used by another process`.

The cause of the 51: the journal's frozen-path `DatabaseContext` built its connection string with `SqliteConnectionStringBuilder`. That string differs textually from the app's `Data Source=…;Cache=Shared;`, so the same file got a second connection pool that nothing cleared. Both now use one format. With only that and the two harness fixes, **170 / 170** passed (`resume-related-pool-fix-20261020.trx`), including the four crash cases that fail without a journal.

## Defects found in the journal design, and repairs

These were not covered by the handed-over tests; each would have disabled resume saving on a real profile.

| Defect | Effect before repair | Repair | Regression |
| --- | --- | --- | --- |
| Journal pinned the database's absolute path | A moved profile, renamed data root or SUBST/drive alias made every save and replay throw | The journal is bound by location (it sits beside its database); stored paths are rebound on load | PendingJournalReplaysAfterItsProfileFolderMoves |
| Database pinned one journal ID for every row | Deleting or losing the journal file disabled durable saving permanently | Revisions are compared only within the same journal; a recreated journal's records are newer | RecreatedJournalStillSavesAfterAnEarlierJournalWasLost |
| Damaged journal failed closed | One bad sidecar blocked every later save and every replay | The bytes are kept aside as `media_os.db.resume-pending.json.damaged-*` and a new journal starts | DamagedJournalIsRetainedAsideWithoutBlockingReadsOrLaterProgress |
| Replay errors propagated into reads | A replay failure made both catalog and legacy reads return position 0 | Replay failures are logged per record and leave the record pending; the saved row is still read | covered by the damaged-journal case (Open returns 90) |
| Summary from another library threw | Aborted replay of every remaining record | That summary is skipped; the position still applies | ReplayFromAnotherLibraryAppliesThePositionWithoutWritingThatSummary |
| Owner records persisted forever | One record per unit ever claimed, rewritten with an fsync on every claim and save | Ownership is process-local, as catalog sessions already were; claiming does no file IO | existing ownership cases |
| Flush cleared the shared task list | A concurrent close and session-end flush could stop waiting for each other's work | Flush waits for all in-flight work and reports a failure once | existing drain cases |
| `ON CONFLICT` upsert | Fails on a database without the unique (MediaId, EpisodeId) index | Update, then insert if no row matched | JournaledPositionsApplyToAnOlderRollbackJournalDatabaseWithoutTheUnitIndex |

One **pre-existing** fault surfaced through the last regression. EF Core probes existence with a read-only connection, and the app's connection hook ran `PRAGMA journal_mode=WAL` on every open. On a database still in rollback-journal mode (for example one last opened by a build before June 13, when the hook was added), that write fails, so `EnsureCreated` throws. The player then reads position 0 and its saves fail until something else opens the file read-write. The hook now requests WAL only on read-write connections. The before-repair failure is retained in `resume-durability-final-20261020.trx`.

## Verification

- Final affected cohort: **203 / 204** (`resume-durability-cohort-20261020.trx`, 3 minutes 17 seconds). It covers the 12 existing resume classes, the VA cast and MAL sync recovery classes (both open the database through `EnsureCreated`), 9 journal invariants, 4 real child-process crash cases and the real-app case below. The one failure was a test race: `RealShortVideoFinishesLocally…(network: False)` read the database right after `Dispose`, before the earlier unit's accepted pause save had committed (0 instead of 11–15). It passed in both earlier runs; the journal's extra file writes make the race more likely. The test now waits for the persistence drain before verifying, and its class passed **24 / 24** (`native-short-completion-drain-20261020.trx`). Production code did not change between the two runs.
- The 4 real-process crash cases (catalog and legacy, position and completion) hold a real SQLite writer, accept a position in a child process, kill it, and recover the accepted value in a fresh process. The retained before-repair run is `resume-crash-before-20261017.trx` (0 / 4).
- **Real app, second monitor, isolated AppFixture profile** (`packaged-resume-durability-20261020.trx`, 50 seconds): a generated 10-minute video was paused at **36.801 s** and committed. A real writer then held the database while the player was moved and paused at **71.001 s**; that position was in the journal while SQLite still held 36.801. Normal app close took **17.179 s** and logged that the 15-second drain deadline elapsed with progress pending. After the writer released, a restarted app restored **71.001 s**, the SQLite row became 71.001 and the journal emptied. The seeded fixture row stayed at 120.5.
- Python unit tests: **225 / OK in 0.957 s**. CI does not run these (see finding 1).
- No full suite was run locally; the hosted checkpoint remains the full check once this batch is committed.

## Other confirmed findings (not changed in this batch)

1. **CI never runs the Python unit tests.** The workflow only `py_compile`s the three scrapers, so the 225 tests guarding title, unit, audio and evidence matching are not part of any hosted checkpoint.
2. **Python packages are installed unpinned into the user's global Python.** `PythonBootstrapper` runs `pip install drissionpage curl_cffi httpx beautifulsoup4 lxml` with no versions. The tested set on this machine is DrissionPage **4.1.1.4**, curl_cffi **0.15.0**, httpx **0.28.1**, beautifulsoup4 **4.15.0**, lxml **6.1.1**. A new major release would change scraper behaviour on fresh installs.
3. **The tab's DI scope disposes the player's `DatabaseContext` at once.** `PlaybackViewModel` defers disposing it until accepted saves finish. But the context is a transient the tab's scope created, and the container disposes those when the scope ends, so closing the tab disposes it immediately. (This follows from the registrations and container semantics; no test exercises it.) Journaled saves use their own context and are unaffected; the direct-save fallback (memory databases, or a failed claim) is still exposed.
4. **Close and session-end logs report "still pending" even when a position is journaled.** The QA log evidence therefore understates durability. The drain could report "journaled, SQLite commit pending" instead.
5. **Watch Together relay** (opt-in) binds every interface when the URL ACL allows and has no room secret. Anyone who can reach the port and knows a room name can join, receive the URL the host shares and the room's playback messages, and be promoted to host when the host leaves.
6. **No single-instance guard.** Two instances share one database and journal; ownership is per process, as it was before the journal.

Checked and consistent with the product rules: Stream never opens a website automatically (website is its own explicit tier); TMDB is used only when the user supplies a key; MAL tokens are DPAPI-protected; every log line passes the URL sanitizer; the HLS proxy listens on 127.0.0.1 with unguessable session IDs and refuses non-public upstream addresses; the uBlock download is checked against a pinned SHA-256; a temporary-watch lease is released only after its player scope.

## Prioritized plan

1. **Land this batch.** Commit it and let its single hosted checkpoint run the full suite. The new real-app case adds about 50 seconds to that run.
2. **Put the Python tests in CI** (finding 1): one workflow step, under a second of runtime.
3. **Pin the tested Python package versions** (finding 2). A private virtual environment would also stop the app from writing into the user's global Python; that is a larger release decision.
4. **Make the drain logs say "journaled"** (finding 4). Session end could also return as soon as positions are journaled instead of waiting out SQLite.
5. **Stop the DI scope from disposing the player's database early** (finding 3), with a DI-level regression test.
6. **Continue the open checklist gates in their recorded order:** real Windows shutdown and pressured source admission (without repeating the October 17 timed sequence), broader real providers and audio, download storage failures, and the release manifest/packaged smoke tests. Books stay deferred; Arabic cartoons stay last.

## Open questions

- Should the 50-second real-app durability case stay in the hosted suite, or run only as a local qualification?
- Is the Watch Together relay meant to be reachable by anyone on the LAN without a room secret (finding 5)?
- Should the app keep installing Python packages into the user's global Python, or own a private environment?

## Limits

Real Windows shutdown, pressured new-source admission and portable configuration migration remain open in the same checklist group. The October 17 timed desktop sequence was not repeated. No desktop-automation tool was available in this session; the real-app evidence comes from the FlaUI harness driving the actual executable on the second monitor, not from a human or desktop-tool run. Spoken audio, providers, DASH, captions, Books and Arabic cartoons are unaffected by this batch.
