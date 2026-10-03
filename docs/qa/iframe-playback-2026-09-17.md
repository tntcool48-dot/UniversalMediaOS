# Browser audio handoff and iframe playback — September 17–28, 2026

This batch addresses the observed Dub-to-Sub switch and the app timeline staying at 00:00 while provider video played. Automatic EverythingMoe/The Index discovery remains the normal anime route. There is no new provider picker, provider hostname adapter or key requirement.

## Implementation

- Preserve Sub/Dub in the episode playback context through the initial browser handoff, Retry and episode resolution. A bounded browser script uses labelled Sub/Dub controls and reports whether the requested selection was made. An unsupported selector produces a manual-selection instruction rather than a success claim. The selection is page evidence, not independent proof of the audible language.
- Subscribe to WebView2 frame events, including nested frames. Each HTTP(S) frame document gets a distinct token bound to its navigation and origin. Navigation/destruction invalidates that document. Accept playback messages only from the selected credible video frame under the current top-level page; send controls to that document. Keep existing top-document support.
- Feed video progress and pause/play state into the app every second, retaining throttled database saves. A dispatched Play command alone no longer changes the app to Playing. A browser-policy rejection asks the user to press Play in the page.
- Apply pending app resume only to the selected video document. Check the seek result and preserve a failed pending restore for a later valid event. Browser progress no longer sends redundant seeks to the inactive native player.

## Initial live run

Build: `.artifacts/implementation/iframe-playback-build`. Profile: `.artifacts/implementation/index-discovery-manual-20260913-231032`, real services, automatic preparation. This build was prepared September 15 and tested September 17; the dates in its launch manifest and live log intentionally differ.

Requested **Mushoku Tensei: Jobless Reincarnation Season 3, episode 1, Dub**, AniList 178789 / MAL 59193. Automatic resolution ran **18:02:20–18:03:05**, approximately 45 seconds. AniKoto exhausted and Miruro supplied the browser fallback. The log still shows a Part 2 candidate considered during extraction; matching remains open. The browser document loaded at 18:03:07 and reported the Dub selector at 18:03:09. Scrolling the actual page independently exposed **Audio: Dub**, episode 1 “Burn Bright, Mad Dog,” and Season 3.

The selected video iframe origin was `https://strm.cx/`. The app displayed **04:57 / 23:40, Paused** from the site's pre-existing position. This first position is not app-resume proof.

- Initial app Play did not start video; the site Play button did. The app stayed paused until the video confirmed play. The final correction adds an explicit page-Play instruction on browser-policy rejection.
- Video frames advanced and the app timeline reached 05:13. App Pause stopped video at **05:21**; both provider and app showed Paused.
- App **+30s** changed both timelines to **05:51** while paused. App Play then resumed video, with advancing frames and the app timeline reaching 06:08.
- App Pause stopped at **06:18**. Closing the app at 18:05:30 completed without the earlier player-close crash. A read-only SQLite query found media `59193`, episode `1`, position **378.37322 seconds**.
- The log exposed browser time updates trying to set the native player's time. The final correction guards that callback in browser mode.

## Fresh-browser restart check

Prepared `.artifacts/implementation/iframe-resume-manual-20260917` with a SQLite backup of the isolated app database, a config pointing to that backup, and the already-installed extension. **No browser storage or browser profile was copied.** `profile-evidence.json` records the setup. This separates app resume from provider-site storage.

Pre-presentation-build process 24388 used this profile (`launch.json`). The fresh catalog required searching Mushoku Tensei and selecting the Season 3 entry again. Requested Dub; resolution ran 18:11:34–18:12:15. The log records the database load at 378.4s, handoff to the playback bridge, Dub selection, and **application of 378.4s to the selected iframe at 18:12:19**. The app showed 06:18 / 23:40.

App Play reproduced a browser-policy rejection and correctly showed **Press Play in the page**. The first site Play click triggered a blocked popup; a second site Play click started the actual video at **06:18 / 23:40**, with the same scene as before closing. Video and app time subsequently advanced to 06:30. This establishes app-level restoration with fresh browser storage for this same episode/source. No browser-progress-to-native-seek log spam appeared in the final build. Closing the playing browser tab at 18:13:13 saved 398.8s and returned to the details screen without a crash.

## Limits

This is one provider-page/iframe flow. Page audio selection is not an independent listening assessment. Cross-provider/cut timing, site-side episode changes, exact source evidence, broad provider compatibility, and full playback/release acceptance remain open. Source discovery still took approximately 45 seconds; this batch does not claim a resolver speed improvement.

## Presentation-mode failure found on September 18

On resuming the live session, the existing player tab was black with Exit PiP still visible. The log at 00:33:04 showed browser disposal during entry to picture-in-picture followed by an ignored reload. Code inspection found two connected defects: presentation mode still assigned playback to root row 2, although grouped navigation moved content to row 3; changing WindowChrome also generated temporary Unloaded/Loaded events that permanently disposed the browser.

The presentation correction locates the actual playback ancestor under the window root rather than fixing another row number in place. Unload cleanup waits until after WPF Loaded events, but runs before the tab's Background native-player disposal. A transient reload keeps the existing browser document and subscriptions; a genuinely removed view still detaches/disposes. Live checks and exact-build results follow below.

## September 19 live resize failure and correction

The first presentation build passed 450 tests but failed with a real initialized browser. Process 53220 used the same isolated resume profile, with all required service checks ready. Automatic Sub lookup ran 11:30:55–11:31:38 (43 seconds); the browser document/audio acknowledgement arrived at 11:31:40 and the selected iframe restored 344.0 seconds at 11:31:42. A page Play click started video at 05:44, with advancing scenes and app time to 05:55. Scrolling exposed the actual provider's **Audio: Sub** selector.

PiP entry at 11:32:18 crashed in `WebView2CompositionControl_SizeChanged` → `GraphicsItemD3DImage.UpdateSize` → graphics frame-pool `Recreate`, with an invalid-parameter exception. The app shrank before removing navigation; the control rows could consume all remaining height. The repair removes navigation before shrinking, restores it after expanding, and gives the browser surface a one-DIP minimum in each dimension to avoid zero-size capture surfaces. The failed run is preserved as `iframe-playback-live/presentation-zero-size-crash-20260919.log`; the earlier passing TRX is `results/presentation-before-live-resize-fix.trx`. The native/empty-player automation cases did not establish initialized-browser resize safety.

The resize-corrected build (`iframe-presentation-resize-build`, process 24320) passed the full 450-test suite and a live retry. Source lookup took 41 seconds (11:37:09–11:37:50), and the browser restored 354.8 seconds. Video played through PiP entry at 11:38:14 and exit at 11:38:56, then fullscreen entry at 11:39:17 and Escape exit at 11:39:27. The same video showed English subtitles and advancing scenes/time. There was no browser disposal or new top-level navigation between those transitions. App Pause worked and closing saved 452.1 seconds without a crash. However, the full control panel crowded PiP into a tiny video area. The final UI correction hides that panel only during browser PiP, retaining site controls and the persistent Exit PiP button; normal controls return on exit. Native controls keep their existing behavior.

## Final mini-player check — September 28

The final build (`iframe-playback-final-build`, process 2120) passed the full 450-test suite with zero build warnings/errors. In the isolated profile, required service checks passed. Requested Mushoku Season 3 episode 1 Sub; automatic source resolution ran 01:36:15–01:36:57 (42 seconds), then Miruro opened the correct episode. The app loaded its saved 452.1 seconds and applied that position to the selected iframe. The provider page confirmed Sub and playback began at 07:32 after pressing Play in the page.

Entering PiP at 01:37:18 showed a large video area with Exit PiP visible. The scene and app position advanced during PiP; exit at 01:37:30 restored the normal controls and continued the same video without a new top-level navigation or WebView disposal. An initial pause check was inconclusive because the log contains two Play/Pause invocations five seconds apart; a separate single click at 01:38:41 made both app and provider display Paused at 08:55. The app remains open in the paused full player for inspection. The frozen log is `.artifacts/implementation/iframe-playback-live/final-sub-pip-20260928.log`, with its isolated launch manifest beside it. This is one same-source provider/episode run, not full playback or provider acceptance.

## Regression verification

Final presentation build `.artifacts/implementation/iframe-playback-final-build`: **450 passed, 22 existing skips, zero failures**, zero build warnings/errors, September 19 build and September 28 live check. The two added presentation cases verify player visibility, no disposal during entry/exit, and browser cleanup before native disposal on genuine tab closure. The earlier focused presentation/lifecycle selection passed ten cases. Initial harness runs exposed missing WPF resources, hidden controls, concurrent log sharing and shared-window fixture invalidation; those failed TRX files remain under `.artifacts/implementation/results/presentation-*-failure.trx`. Presentation tests now own independent app fixtures and tolerate transient unnamed windows. The final full suite includes those corrections.

Pre-presentation build `.artifacts/implementation/iframe-playback-verified-build`: **448 passed, 22 existing skips, zero failures**, zero build warnings/errors. Nine additional cases cover frame origin/token/readiness, navigation/destruction invalidation, unsupported frame origins, requested-audio acknowledgement, Sub/Dub context through Retry, confirmed play/pause state, and browser-policy rejection. The focused pre-live selection passed 40 tests before the ninth case was added. The full final-source run includes the seek-confirmation correction.

Python is unchanged from the preceding checkpoint (SHA256 `A1B91A1ED131F2866D7D10644AF18F0541F94F3141154853DD0137B57367E30D`); its previous 16-test run is historical evidence, not a rerun in this batch. `git diff --check` passed. Final [TRX](C:/Users/user/animeapp/.artifacts/implementation/results/iframe-playback-final.trx), [executable](C:/Users/user/animeapp/.artifacts/implementation/iframe-playback-final-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), and [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/iframe-playback-checkpoint.json). Logs are frozen under `.artifacts/implementation/iframe-playback-live`.

See the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) for remaining work.
