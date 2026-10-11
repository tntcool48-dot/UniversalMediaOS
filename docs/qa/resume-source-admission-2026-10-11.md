# Bounded source and ownership admission — October 11, 2026

## Reproduced problem

Coalescing already bounded repeated positions within one source interval. It did not bound accepted progress across distinct units or explicit ownership boundaries. The corrected two-path regression made 200 source requests with accepted positions under a real SQLite writer lock. Both paths accumulated **400 pending reads/saves** while preserving their rows: **170.522 ms catalog / 12.488 ms legacy** for the burst. Both bounds failed. [Corrected failing baseline](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/resume-admission-before-2026-10-11.trx).

An earlier version of this fixture mixed the latest unplayed source's seeded resume with a newly supplied position; it failed that expectation before reaching the queue assertion. It was corrected before drawing the queue conclusion. Its failed results remain separate (`resume-admission-initial-2026-10-11.trx`).

## Repair and contract

A player stops admitting additional source/ownership operations at **30 pending operations**, leaving room for final observations. A shared **64-operation admission threshold** applies across players. Under pressure, the player pauses its existing unit and retains one latest **unaccepted** request. Further requests replace only that request. Accepted immutable saves remain ordered and drain normally; the latest requested unit opens when the worker has capacity. Unit identity is not changed to an unaccepted selection.

Native/browser playback displays **“Saving progress before continuing…”**, including an Arabic translation. Seek, transport and selectors are unavailable while waiting; late telemetry cannot reinterpret the old clock as the waiting unit. Deferred sources invalidate stale navigation/quality/caption continuations. Header/subtitle/path inputs are copied for the pending handoff. Normal and session-ending close cancel the unaccepted request, while accepted writes and database disposal retain their existing drain contract.

Catalog and legacy IO now reserve the same serialized progress worker **before** awaiting prior reads/saves. This preserves dependency ordering and avoids one blocked SQLite worker thread per legacy player. The private player context still has its own lifetime gate.

The shared threshold is backpressure, **not a promise that final saves are rejected at exactly 64**. Existing live producers retain up to two final observations each; the supported bound includes that headroom and one pending request per live player. Accepted positions are never dropped to satisfy a count. These limits bound interval accumulation rather than impose a player-tab limit.

## Verification

The final reused Release build passed with zero warnings/errors. **34 affected/related checks passed, zero failures/skips, in 33 seconds**. This covers the five new admission cases and the existing queue/read cancellation, ordered source change, seek, completion, profile capture, normal/session close and deferred context disposal cases. [Final exact-source cohort](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/resume-admission-final-2026-10-11.trx).

- **200 requested units per path:** 15 units admitted; **31 maximum pending operations**; catalog **5.981 ms**, legacy **0.271 ms**. Every admitted position and the adjacent 57-second row persisted. Requested unit 200 reopened from its own **371-second** row. Blocked seek/play shortcuts did not change the held clock.
- **20 mixed catalog/legacy players, 4,000 requested units:** 22 units admitted; **64 maximum shared operations**, **10.872 ms** for the burst. Every accepted row persisted; each independent player restored its own latest target at **371–390 seconds**.
- **200 explicit ownership changes:** **31 maximum pending operations**, final accepted **149 seconds** retained, with the pending play request dispatched after drain.
- **Close before admission:** the unaccepted source task canceled, every accepted row persisted after writer release, and the unaccepted target's seeded 371 seconds remained unchanged.

The earlier 35-case cohort also passed before the final callback/legacy-worker changes; it is not added to the final unique total. A subsequent five-case checkpoint had four passes/one failure: its ownership fixture could skip superseded saves before any real write started, never necessarily reaching pressure. The fixture now reserves the ordered worker with a real locked SQLite save first; assertions were retained. The one-case follow-up and final cohort passed. All intermediate TRX files are preserved separately; passing unchanged broad scopes were not repeated.

Tested source is `24bc0c8` plus this batch. No new physical package, visible monitor journey or OS action is claimed. The actual open private package remains `e036323`. Final WPF DLL SHA-256 **`6DB2B4983990ACE5EB4A40D146561B5F7031F05F9C020BB36817B674E3E5A5D9`**; Core DLL **`FC880E44FC879861FC20E66F7EF8BD871CC20E3B6C5A878CFE8569E2481ACCAE`**. These identify the focused local artifact; its readable version predates publication, so it is not claimed as a newly stamped package.

## Remaining limitations

The waiting notice, selectors and native-source admission still need qualification in the actual packaged window. This controlled matrix is not all-provider/media acceptance. Forced termination, persistence after exit beyond the 15-second normal/three-second session-end deadlines, real Windows shutdown, portable configuration migration and final release remain open. Original profiles, permanent/temporary media ownership, keyless catalog defaults and provider matching were unchanged. Six unrelated untracked paths remain untouched. The grouped persistence gate remains open.

Published on main as **`4c8a2b8964ad4b5095d20978f4983f1679198255`**. Its single [hosted checkpoint](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/38146056964) is in progress at this recorded checkpoint. No full pass is claimed for this source. The preceding completed full source is `9e33b70` (1,221 passes / zero failures / 22 unchanged skips).

### Completed hosted result

That run subsequently failed: **1,223 passed / three failed / 22 unchanged skips / 1,248 total**. Build passed; Python syntax was skipped after the test failure. All three failures are the existing browser-surface hit-testing theory: its extracted production `VideoSurface` now references `PersistenceWaitingOverlayTemplate`, but its isolated resource list does not copy that template, so `XamlReader.Parse` throws before the click assertions. The production view defines the resource; this result does not demonstrate an actual packaged-window startup failure. The test setup needs repair and focused requalification before the latest package can be accepted. Original diagnostics were downloaded once to [the retained TRX](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/ci-4c8a2b8/release-validation.trx). No full-suite retry was started.
