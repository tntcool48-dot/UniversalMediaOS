# Native selector popup focus — October 5, 2026

The user reported that choosing **CC Off** immediately returned to English. The previous short generated caption cases, and four additional cases using the preserved Pilot MKV, did not reproduce that exact reversion. Slower mouse input exposed a related native speed-selector defect: clicking **2x** dismissed the popup with **1x** still selected. The native clock advanced at approximately normal speed. This was a real-app mouse failure, rather than evidence that changing a bound value worked.

## Problem and repair

`PlaybackView.IsControlsInput` originally checked only visual ancestry beneath the title, options and transport panels. A WPF ComboBox popup has a separate visual tree. Its item could therefore reach the player's mouse handler without being recognized as a control. The handler focused the video surface, dismissing the popup before committing the requested item.

The check now follows an item back to its owning ComboBox before continuing through the panel's ancestors. Video clicks retain their focus behavior; selector clicks keep control ownership. No caption preference, provider matching, resume identity or native track-language policy changed.

The unchanged speed/audio regression failed before the repair with **“Playback speed returned to '1x'”**, then passed afterward. An earlier attempt used the wrong test locator (`2` instead of the displayed `2x`); that locator failure is not a product defect. Initial popup-opening timing was also intermittent before the repair, including one paused caption case. A diagnostic rerun passed all four caption cases before the repair, so those caption results alone do not establish that the user's exact CC issue is resolved.

## Verified cases

All live test windows use AppFixture's isolated profile and **StartupMonitor=Secondary**. Playback was muted.

- The new 60-second native fixture contains video and two explicitly named **440/880 Hz tones**, with an English two-line sidecar. It contains no spoken language. Mouse selection measured actual native time advance at 2x and 0.5x, changed rate/audio while paused without moving the position, retained the second audio choice and 0.75x rate through CC Off's native input reload, stayed paused, then resumed at the retained rate and changed audio while playing.
- In the first passing fixed-build run, native advance/wall-clock ratios were **1.772 at 2x**, **0.444 at 0.5x**, and **0.694 at 0.75x after the reload**. These short samples include UI observation and decoder clock granularity; they are not precision rate benchmarks.
- Four fixed-build Pilot cases passed: playing/paused × direct CC button/mouse dropdown, Off followed by English. The dropdown remained open for two seconds before each choice, crossing normal native clock/track refreshes. The borrowed 212,613,476-byte MKV and its existing library caption were opened through the Player's source field and sought to 700 seconds. Position and pause checks passed; the media's length and last-write time remained unchanged.
- The default caption tests continue generating their own small fixture. The optional `UNIVERSAL_MEDIA_OS_QA_NATIVE_MEDIA` override is only for an explicitly supplied local QA file; it does not add a CI dependency on permanent media. Progress remains in the test sandbox. The final tests use valid fictional provider identities, rather than producing invalid-library-summary warnings.

Evidence: [speed/audio failure](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-selector-reversion-2026-10-05.trx), [speed/audio after repair](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-selector-popup-fix-2026-10-05.trx), [fixed-build Pilot captions](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/pilot-caption-popup-fixed-2026-10-05.trx). Raw app logs/test diagnostics remain local and are not published.

## Preservation and limits

A read-only preservation check retained all **six permanent media files, 2,028,915,092 bytes total**, their metadata/captions and separate original positions: Pilot **803.205 s**, S1E2 **132.484 s**, S2E1 **371.022 s**, Dune **318.291 s**. A separate small QA database/profile was prepared for the final Downloads-button check. It references the existing media; no video was copied and no original profile setting or progress was overwritten. Private evidence is under `.artifacts/implementation/player-popup-focus-20261005`.

The popup focus defect has a reproduced failure and passing regression. The user's original CC report still needs confirmation against this build. The opt-in caption test uses the actual file/sidecar but enters through the source field; it does not substitute for every Downloads/catalog/provider journey. Tone metadata and subtitle labels do not qualify spoken audio. Other providers/languages, sustained playback, Windows DPI, packaged execution and broad release acceptance remain open. Books remain deferred, with Arabic cartoons last among the other work.

Tested Debug assemblies after the repair: WPF SHA-256 `2DFFA546950D6008A0E67A79C41C49126B6C34E60428F45D3180195D99FF76FF`; Core SHA-256 `1BF53E3C987CA4FC8FBCE9BE5802D4EE1FD8DEFE003E1D3872090485689BB47B`. Build succeeded with zero warnings/errors.

## Final full regression and Downloads check

The final default full suite passed **852 tests, zero failures and the same 22 skips**, 874 total, in **5 minutes 33 seconds**. This includes the generated speed/audio and delayed-caption cases with the final fictional identities. Python is unchanged; its latest full result remains 178 passes. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/popup-focus-full-2026-10-05.trx). The preceding localization commit `2e14439` separately passed hosted Release validation with 851 passes/22 skips in 3 minutes 16 seconds; it predates this focus repair.

The fixed Debug executable then opened the separate QA profile, PID **40880**, at **2880,478**, **1280×800** on the second monitor. Downloads retained all six original episode cards. Pilot's Play button opened the same permanent MKV and restored the copied **803.205-second** position. K paused at **815.008 seconds**; M muted the player, and Left sought to **805.258 seconds**, visibly rendering an authored two-line English cue while paused.

The separate CC button selected Off, removed the cue and settled paused at **805.655 seconds**. A second click restored English and visibly rendered the cue during reload; playback settled paused at **806.027 seconds**, after that short cue's end. A further paused backward seek to **796.027 seconds** rendered another two-line English cue. The player is left paused/muted with options open for the user's confirmation. The approximately 0.4-second decoder startup drift per caption reload remains.

The desktop helper rejected the popup's accessible items as unavailable in its cached app state. One bounded coordinate attempt dismissed the popup and focused the source field before selection; English remained. That tool attempt is not caption acceptance. The new native mouse regression and opt-in file cases used the existing FlaUI test infrastructure; the human's original dropdown report remains open pending fixed-build confirmation.

The final preservation probe passed: all six originals, modification times, metadata/caption hashes, original config and four original resume rows were unchanged. Only the separate QA database's Pilot position changed. Screenshots and preservation records remain in the small ignored evidence folder; no media copies, cleanup, reset or original database migration were performed.
