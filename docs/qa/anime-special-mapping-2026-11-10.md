# Anime specials: merged regressions, browser profiles, navigation and live players — November 10–11, 2026

Second pass on the special/provider-unit mapping item, on the merged wave-1 code (`codex/app-recovery` at `401155b`, fast-forwarded into `claude/anime-special-mapping-8f51f9`). Earlier evidence: [October 23–24 QA](anime-special-mapping-live-2026-10-23.md).

## Merged code keeps the special-mapping repairs

A fresh Debug build of the merged source passed the focused special/download scope with **197 / 197**. That scope covers the special mapping, special identity, AniList-uniqueness, temporary-watch, temporary-discovery, native temporary-download boundary and download-routing classes. The whole Python suite passed **244 / 244**. The merge broke nothing in this item. No full suite ran.

## Anime lookups leaked their Chromium profiles

`ScraperEngine` (the anime resolver's C# caller) started `scraper.py` with no owned profile folder. `scraper.launch_browser` created `umos_scraper_*` under the shared `BrowserProfiles` folder, and only its own `close_browser` removed it. That removal never ran for:
- a cancelled lookup, since `ScraperEngine` kills the process tree;
- a timed-out lookup;
- a lookup still running when the app closed, which outlived the app with its browser.

A browser still exiting after a normal run could also keep its folder. The only safety net was the 12-hour stale sweep. The October 23 live cancellation had already left one such folder. movie-tv-providers reported 70–97 MB profiles on its own path, which it fixed separately; that path is unchanged here.

**Failing checks first.**
- `AnimeScraperBrowserProfileCleanupTests` covers finished, cancelled and app-close runs, and cancelling one lookup while another keeps its live profile. Its fixture places the profile exactly as `launch_browser` does. Before the repair it failed **4 / 4**, each with the profile left behind.
- `test_anime_browser_profile_root.py` failed **2 / 2**: the profile must be inside the caller-owned folder, and a relative folder is refused.

**Repair (`58f65aa`).**
- `ScraperEngine` gives each run its own `BrowserProfiles/umos_scraper_run_<guid>` folder, passed as `UMOS_SCRAPER_PROFILE_ROOT`; `launch_browser` creates its profile there.
- After every run (finished, failed, timed out or cancelled), the engine deletes that folder. While an exiting browser still holds files it retries, then keeps trying in the background.
- `ScraperEngine` is now `IDisposable`, so the app's service provider disposes it at close. `Dispose` cancels running lookups, kills their process trees and removes their folders. Only this engine's run folders are touched.

**After:**
- **4 / 4** new engine cases;
- **34 / 34** together with the Movies/TV cleanup cases and the engine and Python-preparation tests;
- **246 / 246** Python tests.

## Next/Previous across a special and its parent

`SpecialEpisodeNavigationTests` opens a two-episode SPECIAL through the real `AnimeDetailsViewModel` Watch flow, with a recording resolver behind the real `TripleNetHandoff`, and drives a `PlaybackViewModel` with the episode context it receives:
- **Special, ep 1:** the context is 1–2 with `anime:anilist:194884`. Next resolves the special's own title, AniList/MAL IDs and episode 2, with only its own aliases.
- **Special, ep 2:** this is the last unit, so Next is unavailable and cannot continue into a parent-series episode 3. Previous returns to episode 1.
- **Parent TV series, ep 12:** Next is unavailable. Previous resolves the series' own title and IDs for episode 11 and never a special alias.

Both checks **passed on the first run (2 / 2)**, so no fault was found in this path and nothing was changed (`0e599e3` adds the checks as regressions). The parent-page rejection from October 24 (`07e5ff7`, `5c9c750`) keeps the scraper from accepting a parent page for a special's next unit. Downloaded episodes still disable adjacent navigation by design: each download needs its own explicit choice.

## Live players for specials (November 10, 23:41–23:45 UTC)

These ran under the desktop lock with the production `scraper.py` and the app's exact arguments, the unchanged 63-second budget and 6-site limit, and an owned data root.

| Request | Elapsed | Result |
| --- | --- | --- |
| Kaguya-sama: Love Is War -Stairway to Adulthood- (194884, SPECIAL/2), ep 1, aliased | 19.860 s | **native stream accepted**: Anikoto, `consistent`, `episodes=[1]`, catalog-ID match; third server after two unavailable hosts |
| Attack on Titan: Lost Girls (99634, OVA/3), ep 1, aliased | 18.703 s | **native stream accepted**: Anikoto, same evidence |
| KONOSUBA 3 BONUS STAGE (181244, OVA/2), ep 1, aliased | 67.078 s | `all_indexed_sites_failed` after 3 sites (Miruro `consistent` without ID, no media; Re:Anime `unknown`) |
| alias-free `かぐや様は告らせたい 大人への階段`, ep 1 | 66.984 s | `all_indexed_sites_failed`: Anikoto consistent with alias and ID matches, but no media from its servers within the budget |
| **alias-free, ID-free `進撃の巨人 LOST GIRLS`, ep 1** | **19.250 s** | **native stream accepted**: native-title discovery found one AniList entry; Anikoto `consistent`, whole translated-title (`alias_match`) and catalog-ID evidence, `episodes=[1]`, Sub server |

An alias-free special lookup therefore now reaches an accepted, validated native stream with independent title, unit and catalog evidence and no supplied aliases or IDs. The Lost Girls native request was a different request from the failed Kaguya one, not a repeat of it. These are resolver results: the in-app player for these streams is covered below if it was run. Stream URLs are not recorded. Evidence (summaries and redacted logs) is in `.artifacts/implementation/anime-special-mapping-20261111`. None of these normal runs left a browser profile.
