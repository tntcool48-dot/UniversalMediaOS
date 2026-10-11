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

### Focused browser setup repair

The extracted surface now copies the production waiting template and explicitly binds a non-waiting, active player. The original browser hit-target and hidden-native-overlay assertions are unchanged. **All three previously failing cases pass / zero failures/skips, in 222 ms**, with a reused Release build reporting zero warnings/errors. [Original focused TRX](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/browser-surface-resource-repair-2026-10-11.trx). The unchanged 34-case queue scope and broad local suite were not repeated. This repairs the isolated test setup; actual packaged waiting-state and broader acceptance remain open.

### Latest package and bounded physical attempt

The repair is published as **`dae593fa5041a54986bd621dea7a1084ebe6eb31`**, with one [hosted checkpoint](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/38147549831). Application inputs are identical to the queue repair; only the isolated browser test setup changed. The existing publish folder was reused and both application assemblies identify `1.0.0+dae593fa5041a54986bd621dea7a1084ebe6eb31`. Preparation and subsequent verification retain **187 source inputs / 1,365 published files / 417,584,831 bytes**; every recorded input/file hash and file-set membership passed verification. Earlier manifest, readable identity, launcher and launch receipt were preserved.

The old e036323 process **60692** exited normally at **17:30:57 local**, logging accepted-progress drain. The guarded existing launcher started private-profile **PID 76508 at 14:33:28 UTC**. Actual startup/navigation and service-health Ready passed on the second monitor at **1280×800, origin 2880/478**; Python, HLS, uBlock and FFmpeg reported healthy, while optional qBittorrent was unavailable. Its private Downloads folder has no files; the visible empty state is accurate and is not a saved-file journey.

The native source attempt remained empty: overlay set-value calls reported missing cached targets, focused typing did not appear, and file-picker set-value/keyboard focus remained unreliable after one supported-client reset. Fresh coordinate buttons successfully dismissed the picker and closed the unused player at **17:44:21 local**, with disposal/native-resource release logged. **No native media loaded, no SQLite writer started, and no persistence notice was observed.** This is a desktop-input limitation, not demonstrated player or persistence failure. All **nine existing resume rows and the saved configuration hash remain unchanged**. [Guarded physical/package receipt](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/persistence-candidate-ui-dae593f-20261011.json), [bounded application log](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/persistence-candidate-ui-dae593f-20261011.log). The broader persistence/media/release gates remain open; no passing broad local scope was repeated.

### Completed repaired-source checkpoint

The single dae593f [hosted run](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/38147549831) completed successfully: **1,226 passes / zero failures / 22 unchanged skips / 1,248 total**, with build and Python syntax passed. The original TRX spans **14:34:09.8405599–14:46:40.3353421 UTC**, **750.494783 seconds (12 minutes 30.495 seconds)**; job completion was **14:46:46 UTC**. All 22 skipped test names match the preceding completed 9e33b70 checkpoint. Original diagnostics were downloaded once to [the retained TRX](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/ci-dae593f/release-validation.trx). No duplicate broad local run or unchanged queue-cohort rerun was performed.

The package readable identity and current manifest now record this result; the earlier pending manifest remains preserved. Updating the readable identity changes its recorded size/hash and the published byte total to **417,584,839 bytes**, with the same 187 inputs/1,365 files. [Completed package manifest](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/dae593f-ci-passed-manifest.json). This checkpoint qualifies the repaired test/source scope; the actual waiting notice, native source journey attempted above, exit deadlines and broader release remain open.
