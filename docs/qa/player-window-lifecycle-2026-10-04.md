# Native player window lifecycle — October 4, 2026

The current build passed scoped playing Alt-Tab/minimize/restore, keyboard seek/pause and independent paused-tab return on the **second monitor**. Downloaded caption Off/on selection remains unqualified because the desktop tool dismissed its popup before input. No player source changed in this verification batch.

## Actual journey

The isolated app, PID 21340, used the existing QA profile, `StartupMonitor=Secondary`, at origin **2880,478**, **1280×800**. Downloads → Play opened the preserved **Breaking Bad S1E1 Pilot** local MKV with its own saved **475.544s** position and downloaded English captions. Pausing displayed a two-line cue with native controls above it.

Playback options visibly listed **CC Off** and **English (downloaded)**. Keyboard input targeted the main window while the selector belonged to the native overlay. The direct Off click dismissed the popup and focused the source field; English remained selected and visible. This is excluded from passing evidence and does not establish a product selection defect. The existing caption-switching gate remains open; the app was not rewritten to accommodate automation.

After explicit resume:

- **Alt-Tab** left the playing app; observation while inactive requested accessibility only, without a screenshot of another app. Reactivating the target app returned native video and advancing playback.
- **Minimize** produced the tool's explicit minimized-window state. Restoring the app retained native video, playback and second-monitor placement. A transient Windows restore-animation capture was discarded; the settled target capture was retained. Native controls subsequently hid on idle.
- **K** paused at **586.822s**. **Right** sought forward 30 seconds to about **616.822s**, and **Left** moved back 10 seconds to **606.822s**, while the player stayed paused and rendered the new frame. K resumed explicitly.
- Switching to Downloads paused S1E1. Downloads → Play opened **S1E2 Cat's in the Bag...** at its own prior **106.716s** position, with local English captions. This sample was muted; spoken audio is unqualified.
- Returning between the two players retained their separate frames/captions and stayed paused: S1E1 **615.726s**, S1E2 **132.484s**. Closing the older S1E1 tab preserved the remaining S1E2 frame, captions and **132.484s** position. A fresh observation confirmed it stayed paused.

Switching player tabs pauses inactive media and returns paused. Alt-Tab/minimize retained the selected player's ongoing playback; those observations do not claim automatic pause on app focus loss.

## Preservation and verification limits

Normal app close exited the known QA process. A fresh SQLite read recorded S1E1 **615.726s** and S1E2 **132.484s**, reflecting this playback session. S2E1 **371.022s** and Dune feature **318.291s** remain unchanged and separate. All six permanent videos/captions/metadata remain intact, totaling **2,028,915,092 media bytes**; no owned temporary jobs or unpublished copies remain. Configuration is unchanged, SHA-256 `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`. Books and unrelated untracked work were preserved.

Small target screenshots, launch/paused-position records and the preservation probe remain in [ignored artifacts](C:/Users/user/animeapp/.artifacts/implementation/player-lifecycle-20261004). The tested source/build is image commit `9202c34`; its [GitHub Release validation](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37218283304) passed in 4 minutes 27 seconds, with **837 C# passes, zero failures, 22 existing skips**, build zero warnings/errors and Python syntax validation. Local full-suite results remain **837 passes / 22 skips**; Python's latest full suite remains **178 passes**, unchanged. No source change required another test run for this documentation batch.

Browser/mixed-tab state, browser PiP recovery, physical caption/audio selectors, supported sizes/themes/DPI/RTL, full-duration playback, repeated resource settling and packaged acceptance remain open. These native local-file samples do not qualify every stream/provider or audio language. Books stay deferred until other non-cartoon recovery work is complete; Arabic cartoons remain last.
