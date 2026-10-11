# Native paused-clock consistency — October 11, 2026

The actual 6375d52 packaged Sintel stream showed 185.146 seconds while its pause callback saved 188.083 seconds, a 2.937-second discrepancy. That physical sample remains evidence for the earlier executable; the source repair below awaits updated-package physical qualification.

The native pause callback saved the live LibVLC clock but left the visible clock at its last progress notification. It now takes one final position snapshot, updates the display under the existing player-update guard, and uses that snapshot for persistence and peer pause publication. The guard prevents a display refresh from becoming a user seek. Existing buffering-failure, persistence-waiting, completion and unit-ownership guards remain.

A real generated-video decoder regression models sparse progress notifications while native frames and time continue advancing. Before repair it failed with a 2,499 ms visible/native difference. After repair it confirms the final paused display and durable position agree to 1 ms, playback stays paused, no backward seek occurs, and an adjacent resume row stays at 57 seconds. This is a controlled notification-gap sample, not a claim about the frequency or cause of every provider's sparse events.

The regression plus locked-resume startup, paused HLS frame seeking and both playing/paused buffering-timeout cases pass: **five passes, zero failures/skips, 13 seconds**. The reused Release build completed successfully. No broad local suite ran. Results: [before](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-before.trx), [after](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-after.trx).

The live private app/profile was left intact. The clock and browser-cookie repairs are prepared as one coherent source batch; the current executable remains 6375d52. Its next coherent package needs physical pause/display/durable-position qualification. Native TV restart still needs a verified available source. The count remains 108 checked steps / 17 open groups, including two deferred Books groups.

## Actual first-package return and follow-up

Source 567cb7e / PID 65228 restored Sintel at 188.083 seconds and rendered changing native frames. K paused it at displayed/saved 192.847 seconds. A later tab-leave save observed 193.115 seconds while the returned player still displayed 192.847, exposing a remaining 268 ms difference. The Archive transport buffered slowly; its immediate late Buffering 64% overlay subsequently cleared to Paused on return. This sample does not qualify sustained streaming speed.

The same process resolved independently matched original Avatar S1E2 through the actual catalog, rendered native 4:3 animation, restored its separate 418.197 seconds, and paused at displayed/saved 443.842 seconds. S1E1 remained 747.957 seconds; the ten other rows and configuration stayed unchanged. No spoken-audio or full-caption claim is added.

A simple delayed local pause did not reproduce the later clock drift. The regression was strengthened with a real paused native seek while progress notifications remain sparse; tab-leave then failed with a 2,000 ms display/save difference. The follow-up synchronizes the display to the snapshot used by forced tab/source/close saves, using the existing no-seek guard. Playback errors and persistence waiting retain their displayed clocks. The extended case plus the same four related cases pass: five passes, zero failures/skips, 13 seconds. [Return failure](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-return-before.trx), [follow-up](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-return-after.trx).

The still-running 567cb7e full checkpoint was cancelled as superseded; it is not a passing result. Only the final source will receive the completed full checkpoint. Follow-up packaged pause/tab-return qualification remains pending.

## Final packaged forced-save qualification

Source 89c3e29 / PID 61232 was published to main, rebuilt in the existing package folder and launched through the guarded private-profile launcher. All 187 source inputs and 1,365 published files passed complete hash/file-set verification; both Core/WPF product versions match the exact source. The single final [hosted checkpoint 38170293309](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/38170293309) passed **1,236 tests / zero failures / 22 exact unchanged skips / 1,258 total** in **12 minutes 26.011 seconds**. Build and Python syntax passed. Original release diagnostics were retained once in `ci-89c3e29`; no passing broad local scope was repeated.

Actual second-monitor Sintel catalog → verified Archive → native stream restored the retained 193.115 seconds and rendered changing frames. After K pause, a wait for the native clock to settle, TV tab leave and forced save, the film returned paused with its retained frame and Play button. The exact seek-bar value was 200,698 ms and the fresh SQLite film row was 200.698 seconds: zero difference. All eleven other rows and configuration stayed unchanged. Private receipt: `.artifacts/implementation/package-smoke-20261009/native-clock-physical-89c3e29-20261011.json`.

This closes the demonstrated normal pause/forced tab-leave display-save discrepancy. It does not claim uninterrupted Archive throughput, full-duration captions/audio, every native format, or full release acceptance. Further TV unit/provider acceptance continues separately; Books remain deferred.

## Final-package adjacent/two-season paused return

The same 89c3e29 second-monitor process selected original Avatar (TVmaze show 555 / IMDb tt0417299), S1E1, through the normal retained catalog Details and verified native source. It restored its own 747.957-second row, rendered changing original-animation frames and a two-line English cue, then paused at **817.379 seconds**. Fresh tab return showed Paused, retained frame/cue, Play and exact seek value **817,379 ms**, matching SQLite.

Explicit Season 2 / Episode 1 (Avatar State) independently resolved a verified native source and began at zero with its new `season:2:episode:1` row. It rendered changing original-animation frames and English cues, then paused at **113.887 seconds**. Paused tab return retained the frame/cue, Play and exact **113,887 ms**, matching SQLite. S1E2 remained **443.842 seconds**; Sintel subsequently returned paused at exact displayed/durable **200.698 seconds**. Ten unaffected prior rows and configuration remain unchanged. Receipt: `.artifacts/implementation/package-smoke-20261009/tv-two-season-paused-89c3e29-20261011.json`.

Initial S2E1 captures showed a white band in the left letterbox area. That band was black after paused tab return; its cause remains unestablished and no rendering repair is claimed. S2E1 new-process restore, provider-change acceptance, spoken audio, complete captions and sustained availability remain open. This finite sample does not add a completed broad group or a manufactured checklist step: **110 checked / 17 open groups / 15 non-Books** remain.

## New-process S2E1 availability boundary

Normal close of PID 61232 drained and retained all 13 exact resume rows and configuration. The same source-stamped package restarted autonomously as PID 7708 on the second monitor. TV My Library reopened original Avatar (2005) with Season 2 / Episode 1 selected and a 1:53 resume summary. One normal Find sources lookup returned **0 verified / 4 unverified / 0 native streams / 4 explicit website fallbacks**. No native player or website was opened. All 13 rows/configuration remain unchanged. Receipt: `.artifacts/implementation/package-smoke-20261009/tv-season-restart-unavailable-89c3e29-20261011.json`.

The previous finite native S2E1 opening/pause/return remains valid for its earlier available lookup. This new-process attempt does not prove native restore and does not establish the provider failure cause. The S2E1 restart gate remains open; the same lookup is not repeatedly polled or counted as a pass.
