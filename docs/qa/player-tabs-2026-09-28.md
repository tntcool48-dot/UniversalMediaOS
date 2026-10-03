# Retained player tabs and site controls — September 28, 2026

This checkpoint implements batch A's tab lifecycle and the site-panel portion of B from the [watchability audit](C:/Users/user/animeapp/docs/qa/watchability-audit-2026-09-28.md). Whole anime playback and the release remain unaccepted.

## What changed

Each open player now keeps its own `PlaybackView`, native HWND host and browser document in the main content grid. Selection hides the outgoing view without reparenting or rebinding it. The outgoing player pauses; returning does not request Play. Late browser autoplay/progress, native startup events, remote synchronization and automatic next-episode events cannot restart an inactive player. Removing a tab releases that view before disposing its player scope.

Window activation and returning to a tab check the actual native host handle and repair the LibVLC binding if the host changed. Returning also reveals the native controls so the paused video can be resumed explicitly. This addresses the shared-view lifecycle behind the audited white surface; it does not establish that every remote stream or graphics configuration is repaired.

The entire app transport/source panel collapses in site playback, including pointer interaction and presentation-mode changes. The site's video controls remain usable. Native loading/error overlays also collapse for inactive tabs. These overlays live in a separate LibVLC window: hiding the parent tab alone was insufficient. The [LibVLC foreground-window implementation](https://raw.githubusercontent.com/videolan/libvlcsharp/3.x/src/LibVLCSharp.WPF/ForegroundWindow.cs) shows its separate load/unload lifetime; the view now explicitly disposes the native surface on final close.

A queued native video no longer fails a startup deadline before its first Play; actually starting it arms a new watchdog. Two browser players exposed a separate reproducible failure: each added uBlock to their shared profile, aborting the first page with `ConnectionAborted`. Extension setup is now serialized and reused after a successful initialization. Failure is not cached, so another view can retry. Final live logs show one extension load, one reuse and both documents loaded.

## Build and automated checks

The final solution build completed with zero warnings/errors. The final full suite passed **470 tests, with 22 existing skips and zero failures** (13 additional passing cases since the preceding 457-test checkpoint). The focused player/startup/retained-handle suite passed 19 cases before the final control-reveal adjustment.

The new tests cover background pause and explicit resume, late autoplay/progress isolation, queued native startup, native-overlay hit testing, removing the site's bottom layout row, 20 repeated switches preserving two distinct native handles, selection of another view template and closing an inactive player without disposing the active view. Existing player disposal, browser/frame, presentation and surrounding-feature checks remain in the full suite. Python extraction was not changed in this batch; the earlier 23-case result belongs to the preceding checkpoint.

Artifacts: [final executable](C:/Users/user/animeapp/.artifacts/implementation/player-tabs-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [final TRX](C:/Users/user/animeapp/.artifacts/implementation/player-tabs-build/results/player-tabs-final.trx), and [source/build/evidence manifest](C:/Users/user/animeapp/.artifacts/implementation/player-tabs-checkpoint.json).

## Controlled desktop playback evidence

The isolated fixture is under `.artifacts/implementation/player-tabs-live-20260928`. Its diagnostic executable references the exact production App, MainViewModel and PlaybackView assemblies and opens two local native MP4 videos plus two same-origin iframe pages with VP9 WebM video. Each clip lasts three minutes; the animated pattern includes a visible time/frame counter. The copied production WPF assembly's SHA256 matches the final build. SQLite's copied native runtime was loaded by the harness; no production storage change was required.

The fixture exercises real native decoding, WebView2 frame telemetry, playback commands, the production tab host and normal cleanup. It bypasses provider discovery. Silent generated media cannot prove anime identity, English audio, subtitles or provider availability. The user's earlier running app/profile was left intact.

| Check | Observed result |
| --- | --- |
| Four open players | Two native sources and two iframe documents retained independently. Initially inactive native sources stayed queued; inactive browser autoplay was paused. |
| Native/native return | Native 1 paused at about 16.4 seconds while Native 2 played. Returning showed Native 1's retained frame counter, approximately 16.792 seconds/frame 403; Native 2 paused around 9.7 seconds. No source reload or white video appeared. |
| OS Alt-Tab | Switched away with Alt-Tab, then activated the fixture again. The paused Native 1 frame remained visible at the same counter. This was a paused local-video check, not a playing remote-HLS check. |
| Site/site return | Browser 2 returned paused at 134.9 seconds. Browser 1 played through its own page control, then returned paused near 9.9 seconds with the same visible frame and document. Logs did not show another page setup/navigation on ordinary tab selection. |
| Site fullscreen | The provider's fullscreen control filled the 2560×1440 monitor with its video and controls. The duplicate app panel was absent. Escape returned to the same paused page/position. |
| Inactive native close | Closing Native 2 released its view/scope while Browser 2 stayed usable and retained its paused position. |
| Inactive/active browser close | Closing Browser 2 preserved Browser 1. Closing Browser 1 selected the retained Native 1, still displaying its paused frame after the browser fullscreen transition. |
| Final build follow-up | After the last adjustment to reveal controls on return, repeated native Play → browser selection → native return → explicit Play. It returned paused at 00:23 with visible controls, then rendered advancing frames from that position after Play. |
| Shutdown | Normal fixture exit disposed all four players and exited. Two already-disposed browser cleanup warnings were logged on main-window shutdown; no crash occurred. Long-run process/memory settling remains unmeasured. |

The broader desktop matrix ran on the candidate containing retained tabs, overlay isolation, site-panel hiding and shared extension setup. The final build adds the one-line control reveal on return and repeats the native pause/return/resume check; the final regression suite targets that exact build. No final-build PiP, real-monitor DPI or minimized-window matrix is claimed.

Earlier attempts are retained in the fixture log. An initial MP4-only browser fixture showed unavailable media; WebM replacement allowed real browser decoding. This is not a claim that general browser MP4 playback was repaired. A test initially referenced a nonexistent local file and failed source validation; its fixture was corrected to an existing queued placeholder, without starting a decoder. Live input occasionally overlapped, and one native-surface coordinate action returned a monitor error; observations were refreshed before continuing. A later overlapping input advanced Browser 1 to about 12 seconds; that position is not attributed to the automation's pause/return test.

## Still open

Native controls still occupy the bottom layout row and changing their visibility resizes the video. Batch B must move them into the native overlay, implement idle/cursor behavior without layout changes, and verify native fullscreen/PiP and accessible recovery. The new site-panel rule applies in every mode, but final-build browser PiP still needs a live check.

Batch A's broader acceptance remains open for real anime native/browser routes, playing-video focus changes, minimize/restore, resource settling, multiple monitors and DPI changes. The native caption handoff and the Japanese-only stream accepted for Dub are unchanged. Complete B and C, then the exact-unit/performance/anime gate, before advancing to Movie/TV watchability, temporary episode ownership or books/cartoons acceptance.
