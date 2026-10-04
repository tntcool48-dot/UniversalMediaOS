# Browser PiP and mixed player tabs — October 4, 2026

The browser window could restore from picture in picture with blank rendered content. Its log also claimed the selected player tab had become inactive. Changing WPF window chrome briefly changes the view's visibility; the handler interpreted that transition as tab deactivation and reentered presentation restoration.

The handler now requires an actually inactive player before restoring presentation for a tab change. Fullscreen/PiP exit clears its mode before changing window chrome, preventing recursive exit. The existing regression now rejects the spurious inactive-tab restoration. Its PiP case failed before the fix; all three presentation cases passed afterward. Actual tab switching still pauses players and restores presentation when appropriate.

## Verification

- Focused presentation, browser startup, retained-tab and pause checks: **27 passed**, zero failures/skips. [Results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/browser-pip-focused-2026-10-04.trx).
- Full suite: **837 passed, zero failures, 22 existing skips**, 859 total, 2 minutes 33 seconds. [Results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/browser-pip-full-2026-10-04.trx). Python production/tests were unchanged; its latest full result remains 178 passes. Build had zero warnings/errors.
- The private startup harness references the current production assemblies in the normal Debug build directory, without another copied app build. WPF SHA-256: `B477F8CD4B6578D3214B7DC3B98B45217C97F34AFE59E0C37A8266DE6411BB57`; Core: `B8F305300F3E70E874A65061FDD05883E65E1D6661E1C565FC1D140F3B7838F8`.

## Second-monitor observations

All visible checks used the isolated retained-tabs fixture, with the normal window at **2880,478 / 1280×800** and PiP at **3910,988 / 548×384**. The harness opens two generated native MP4 clips and two local iframe WebM documents through production App/MainViewModel/player views. Provider discovery is bypassed. These silent clips qualify lifecycle behavior, not real media identity, audio, captions or provider availability.

| Journey | Observed behavior |
| --- | --- |
| Playing browser → P → Exit PiP | Browser 2 rendered in PiP at 40.9 seconds; exit restored navigation and rendered video at 67.9 seconds, advancing to 80.9. No duplicate app transport/source panel appeared. No spurious inactive-tab restoration was logged. |
| Paused browser → P → Escape | Browser 2 remained paused at 90.9 seconds; Escape restored the normal window and same page/frame. |
| Independent browser restart positions | A fresh fixture process restored Browser 1 to 12.0 seconds and Browser 2 to its separate 90.9-second position. Explicit page Play advanced Browser 1 to 21.1 and beyond. |
| Browser/native/browser return | Browser 1 returned paused at 55.8 seconds with its visible frame counter. Browser 2 returned separately paused at 113.2 seconds. Native 1 rendered after explicit K and returned paused at 62.125 seconds/frame 1491 with the same native handle. |
| Older browser close | Closing Browser 1 disposed only that player. Browser 2 retained its paused 113.2-second page; Native 1 retained its frame and resumed explicitly through K, visibly advancing to 72.625 seconds/frame 1743. |
| Shutdown and fixture ownership | Normal close disposed remaining players and the app exited. Both owned HTTP helper processes were stopped. The fixture's original config was restored byte-for-byte: SHA-256 `1D245B3A5DF566D549E3E181ED609CB7B735F7104CEC194A5EDE22CC29F1AE29`. User profiles and permanent media were not used or modified. |

Private evidence is in `.artifacts/implementation/browser-lifecycle-20261004`, including before/after PiP and paused native screenshots, launch records and helper logs. The existing fixture's app log records positions, document setup and native handles. Raw app/profile artifacts are not published.

The first process used Python's basic HTTP server. Browser 1's larger clip repeatedly stayed near zero and failed its saved seek, while the smaller Browser 2 clip worked. After introducing byte-range support in the **private fixture server** and restarting, both restored and played. The restart also reused the browser cache; this establishes the final fixture result, not an isolated proof of the earlier media failure's cause. No product resume rewrite was made. A HEAD probe demonstrated correct 206/Content-Range behavior. Rejected bounds/capture inputs and stale accessibility snapshots were refreshed and excluded from acceptance; visible coordinates were used where indexed tab clicks did not change selection.

Supported size/theme/DPI/RTL checks, real provider browser routes, caption/audio selectors and sustained resource acceptance remain open in the checklist. This batch does not close their grouped player task or the release goal.
