# Compact anime Details — October 5, 2026

At 900×560 and 140% workspace zoom, the old fixed poster/sidebar columns left only 161 physical pixels for the main Details heading and watch controls. The actual title broke into short lines; the body could not scroll through all actions.

Details now uses a bounded poster and moves ancillary cards below the main column when its WPF width is under 1,000 units. One outer body scrollbar reaches watch choices, episode buttons, synopsis, tracker/status and voice discovery. The fixed header wraps episode/MAL status. Theme surfaces replace dark fixed backgrounds under light-theme audio/ID labels, and poster-overlay text stays light against the dark gradient. Commands, media identity and source/audio evidence are unchanged.

## Verification

The real-app compact journey regression failed before the repair on the 161-pixel heading width. Afterward it passed usable heading width and scrolling to the last Details action, following the existing exact search-card selection.

Build: zero warnings/errors. WPF DLL SHA-256: `C4EBE92EB2DCF75146AB7A19F9714F81CB113EA5842B34352E4A38EE9A756376`; Core DLL: `0547BAE95EE959FB8A5F84D30E5AA2FBBC4327607C4E8023E99AEFA9445003DD`.

The final full local suite passed **850 C# tests, zero failures, 22 existing skips, 872 total**, in 4 minutes 20 seconds. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/compact-details-full-2026-10-05.trx). Python source is unchanged; its latest full suite remains 178 passes. This test count is not full physical or release acceptance. The preceding search checkpoint `f8d4ca6` also passed [GitHub validation](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37260891593): 848 passes and 22 skips; those hosted results predate this Details change.

Actual supported desktop interaction used the reused Debug output and isolated existing QA profile on the **second monitor**. At light/140%/900×560 (origin 2880,478), “You and I Are Polar Opposites Season 2” fit in two lines. Its AniList/MAL IDs and progress header were readable. Scrolling reached Sub/Dub, the provider evidence notice, Stream episode, Watch via download, Season Download and episode buttons. Selecting episode 2 changed the header to **Episode 2 of 13**. The final Open VA Detect action remained reachable at the bottom.

Maximizing on that monitor (origin 2560,362; 1920×1040) restored separate poster/main/sidebar columns with the same selected episode, readable watch controls and synopsis. A Settings appearance switch to dark, restore to 900×560 and return to Details retained episode 2 and readable title/IDs. Stream/download/voice actions were inspected for reachability; they were not invoked in this layout batch. Prev/Next were correctly disabled for this single episode page; multi-page navigation remains unqualified here.

The QA app closed normally. The original configuration was restored byte-for-byte (SHA-256 `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`). All six permanent videos, 2,028,915,092 bytes, captions and exact unit metadata remained intact. Positions stayed S1E1 718.160 s, S1E2 132.484 s, S2E1 371.022 s and Dune feature 318.291 s. No temporary jobs or unpublished copies remained. Private screenshots and logs are under `.artifacts/implementation/player-selectors-20261004/`.

## Caption report and limits

Two additional real-app regression cases click the caption dropdown and its Off/English items with mouse input while playing and paused. Together with the direct CC cases, all four passed with a generated silent video and two-line downloaded sidecar, including a subsequent decoder refresh. [Results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/caption-dropdown-before-2026-10-05.trx). The suggested popup/focus cause was **not reproduced**, and no speculative production caption change was made. The human-reported immediate reversion remains open; the direct CC button's actual Pilot checks are recorded separately.

Actual Windows DPI/RTL, broad titles, long provider status, real caption-popup behavior, spoken audio, stable resources and packaged acceptance remain open. These layout observations do not qualify a stream/download journey or whole-feature completion. Books remain deferred and Arabic cartoons last.
