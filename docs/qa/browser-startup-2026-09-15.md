# Anime browser startup investigation — September 15, 2026

The user's observation was correct: the tested source could play when left available and its Play button could receive input. The earlier 30-second error did not establish that the source was unavailable. This batch repairs browser interaction and the misleading timeout; it does not claim that source discovery is now fast or that iframe playback reporting is complete.

## Why startup appeared broken

The September 15 real-service run selected Mushoku Tensei Season 3, episode 1, Dub, through the automatic EverythingMoe/The Index flow. These times are from the isolated app log, not estimates from configured budgets:

| Phase | Local timestamps | Observed time |
| --- | --- | --- |
| Start resolver and refresh indexes | 21:58:44–21:58:47 | About 3 seconds |
| AniKoto extraction, then exhaustion | 21:58:47–21:59:12 | About 25 seconds |
| Miruro HTTP and dynamic browser search | 21:59:12–21:59:22 | About 10 seconds |
| Miruro extraction and browser handoff | 21:59:22–21:59:29 | About 7 seconds |
| Create browser player, configure it, load document | 21:59:30–21:59:33 | About 3 seconds; measured WebView setup 1,115 ms |
| Old timeout threshold, now a soft status | 22:00:01 | Page remained available |

The first provider performed multiple extraction stages and five server-control clicks before moving on. It also considered a Part 2 candidate, an existing matching defect. Miruro's dynamic search waited on an unsuccessful URL before finding the correct card. Its candidate media response was JPEG, so the resolver handed the page to WebView. The hidden extraction browser and the visible WebView are separate browser contexts: loading in the former does not make the latter already loaded. No claim that the five clicks target the identical control follows from their truncated log markers.

`git show HEAD:UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs` lacks the new startup watchdog; it is part of the uncommitted recovery changes. The 30-second browser branch called `ReportPlaybackError`. It did not terminate WebView, but displayed an error over a page that might still work. Independently, the full-size transparent WPF status overlay intercepted input even when its visible panels were hidden. Root input handlers also took focus from the browser.

## Changes

- Remove the status overlay above WebView; put real browser errors and Retry below the page. Retain native-video overlays and native startup failure behavior.
- Treat missing browser telemetry after 30 seconds as waiting, leaving the page available. Mark document load separately from actual video playback and log WebView setup time.
- Preserve browser mouse and keyboard focus. Keep the browser control area present so pointer movement does not resize the page and move provider buttons. Native control auto-hide is retained.
- Notify navigation on same-URL Retry after resetting playback state; otherwise the observable URL setter suppresses the reload.
- Reuse the initialized WebView environment on Retry. The live recheck exposed an exception when a new environment was passed to an already-initialized control. Explicitly collapse the native overlay template in browser mode too: LibVLC's separate overlay window could otherwise reappear over WebView on a real error.
- Limit the extra Exit PiP button to browser mode; its container has no hit-test background.

No provider list, key requirement, timeout setting, metadata migration or Python extraction change was added.

## Live evidence and limits

The first repaired build, `browser-startup-build`, used the existing isolated profile `.artifacts/implementation/index-discovery-manual-20260913-231032`, with real services and automatic preparation. The selected Miruro page retained AniList ID 178789 and episode 1, “Burn Bright, Mad Dog.” After the 30-second threshold, the page's Play button opened a player with runtime **23:27**. Subsequent observations showed advancing anime frames and **0:55 / 23:27**; clicking the provider video paused it. This directly overturns the earlier conclusion that no video could start on this source.

The first click also exposed a viewport shift: showing app controls shrank the page and moved its Play button. A subsequent click reached the control. The final build disables browser control auto-hide to remove that shift.

The app's own timeline remained at 00:00 and its status stayed waiting while the site played. Its telemetry is injected into child frames, but the host only subscribes to the outer `CoreWebView2.WebMessageReceived`; commands also search only the outer document. WebView2 exposes a separate event for frames, as documented in [Microsoft's frame guide](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/frames) and [frame message event reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2frame.webmessagereceived?view=webview2-dotnet-1.0.3124.44). Frame telemetry/commands, safe source/session validation and resume remain the next focused repair. Do not relax the existing origin/token checks just to accept arbitrary iframe messages.

The final-layout recheck used `browser-startup-final-build` and again resolved in 43 seconds (22:07:38–22:08:21). WebView setup took 787 ms and document load completed at 22:08:23. After the soft timeout, one click at the observed Play position started video without a viewport shift. The site restored its own 55-second position; this is not proof of app-level resume. Scrolling the page exposed the audio selector: it said **Sub**, despite the app requesting **Dub**. Audio preference handoff is therefore a confirmed defect; audible language was not independently assessed.

At 22:09:39, the live Retry action exposed the different-environment exception and the native overlay window reappearing over the browser. `browser-startup-verified-build` adds the environment-reuse and native-template visibility corrections. The preceding build is retained as evidence of the failed Retry check.

In the verified build, automatic resolution ran from 22:33:41 to about 22:34:23, with the browser document loaded at 22:34:26. **Retry at 22:34:41 succeeded:** setup reused the existing environment in 1 ms and document load completed at 22:34:42, without the previous exception or overlay. Clicking the site Play button again displayed video and subsequent frames advanced. The provider restored its own position around 4:45 and reported 23:39 runtime in this attempt, differing from the earlier 23:27; neither the restored position nor that runtime establishes app resume or a verified exact audio/cut match. The final test app remains open for inspection.

Correct selected page and advancing video do not establish final audio, exact source matching, app-level controls/resume or whole playback acceptance.

## Verification

The initial focused run passed 39 tests. Its full suite passed 438, with 22 existing skips and zero failures. The final exact-build full run passed **439 tests, 22 existing skips, zero failures**, with zero build warnings/errors. Six new cases exercise late playback, stale timeout state, native timeout preservation and actual WPF hit testing of the production browser surface during loading, waiting and errors. The hit tests also check that the native overlay template itself collapses in browser mode. A seventh case checks that same-URL Retry triggers navigation with initialized state.

An intermediate run failed three new overlay assertions because the standalone template fixture had not joined the visual tree or processed queued WPF bindings. Attaching it and pumping the dispatcher fixed the fixture; all seven focused cases and the final full suite passed. The failed TRX is retained as `results/browser-startup-overlay-fixture-failure.trx`.

Final [executable](C:/Users/user/animeapp/.artifacts/implementation/browser-startup-verified-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [TRX](C:/Users/user/animeapp/.artifacts/implementation/results/browser-startup-verified.trx), and [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/browser-startup-checkpoint.json). The final manual launch uses PID 61480 and `browser-startup-verified-launch-20260915.json` in the isolated profile. Frozen app logs are under `.artifacts/implementation/browser-startup-live`.

See the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) for current acceptance and the next action.
