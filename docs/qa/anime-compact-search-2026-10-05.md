# Compact anime search — October 5, 2026

The actual 900×560 window at 140% workspace zoom previously left no visible anime cards: wrapped filters consumed the content height. The filter panel now has one bounded scroll area, reserving 120 WPF units for results. Pixel scrolling lets a card taller than that viewport expose its title and badges. Row recycling, width-based reflow and commands remain in place; paging prefetches within one viewport of the bottom.

## Verification

- A real-app regression on the secondary monitor failed before the repair: its shorter mock filter collection still left only 99 physical pixels for results. After the repair, the same test passed, scrolled to the exact mock title and opened that item's Details tab.
- **42 focused C# passes, zero failures, five existing skips**, including the compact journey, search/reflow and control/layout checks. Build: zero warnings/errors. [Results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/search-compact-focused-2026-10-05.trx).
- Actual supported desktop interaction used the reused Debug executable, isolated existing QA profile and **second monitor** (origin 2880,478). At light/140%/900×560, cards were visible. Mouse-wheel scrolling exposed “You and I Are Polar Opposites Season 2” and “Overgeared” titles/badges; selecting the first card opened the matching title/poster Details tab. Returning to search retained usable cards. The independently scrolled filters exposed Sort and genre tags; Adventure selection settled to 36 filtered results with changed cards. Search also appended a page to 72 items during the return/paging check; this is scoped evidence, not a full paging/resource qualification.
- The QA app closed normally. The original QA configuration was restored byte-for-byte (SHA-256 `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`). All six permanent videos (2,028,915,092 bytes), unit metadata and caption files remained intact. Positions remained S1E1 718.160 s, S1E2 132.484 s, S2E1 371.022 s and Dune feature 318.291 s. No temporary jobs or unpublished copies remained.
- WPF DLL SHA-256: `D857A40434DDCCB8A571C14CF55D44887B6F52FD99A68C3561EF13236813CE5B`.

Small private screenshots and logs are under `.artifacts/implementation/player-selectors-20261004/`; media, profiles and raw captures are not published.

## Limits

Details is still cramped at this minimum size/maximum zoom: its large heading breaks into short lines and more content needs scrolling. This repair qualifies search-card reachability, not the whole Details layout. Actual Windows DPI, Arabic/RTL search, broad resource cycles and the reported native caption-dropdown reversion remain open. The direct CC button's earlier qualification does not resolve the original dropdown report.

The prior theme checkpoint `5f293d0` passed [GitHub validation](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37259912314): 847 C# passes and 22 existing skips. Those hosted results predate this search change. No new full local suite or Python run was claimed for this scoped batch.
