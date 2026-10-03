# UniversalMediaOS — clean-chat handoff

Snapshot: October 1, 2026. This is context for a replacement chat, not another active checklist. No application code was changed to produce this handoff.

## Start here

- Workspace: `C:\Users\user\animeapp`; Windows/PowerShell. Repository: https://github.com/tntcool48-dot/UniversalMediaOS.
- Stack: WPF/.NET 9, Python scrapers, LibVLC native playback, WebView2 website playback.
- Branch: `codex/app-recovery`; last commit: `715629d` (`Fix scraper failover and proxy-readable streams`). **Extensive subsequent implementation is modified/untracked locally. GitHub and HEAD do not represent the current app. Preserve the working tree, deletions, tests and user data.** Do not reset, clean, replace the checkout or roll back broadly.
- Read `docs/IMPLEMENTATION_CHECKLIST.md` first (current summary, next action and open items), then `docs/qa/movie-tv-item-readiness-2026-10-01.md` (latest evidence). Consult relevant sections of `docs/IMPLEMENTATION_PLAN.md` for contracts and `docs/APP_RECOVERY_PLAN.md` for original requirements. Avoid rereading every historical report or starting another general audit.
- The checklist is the **single progress tracker**. On October 1 it was shortened from 7,842 to 1,323 words, merging overlapping open tasks and removing superseded failure claims. Detailed steps moved to `docs/qa/implementation-checklist-history-2026-10-01.md`, which is historical rather than authoritative. Movie/TV progressive UI wiring and source preference are implemented; scoped native Sub/Dub samples exist. Inspect current code/evidence before reimplementing those repairs. Broader acceptance gates remain open.

## What the user wants

Recover a usable media app after UI regressions, slow resolution and unwatchable media. Deliver the agreed core work without side quests, unnecessary abstractions or rewrites. Work autonomously within this scope; after each coherent batch explain the actual problem, fix, verification and remaining limitations, and update the checklist. Do not equate test counts with completion or ask repeatedly for permission already granted.

Firm requirements:

- **All visible app work/tests on the second monitor.** The main monitor is in use. Prefer isolated test profiles; preserve real profiles, libraries, imports and secrets.
- Normal watching uses a native stream or app-played downloaded file. **Open website is an explicit last resort**, never automatic success for Stream. Streaming and downloading are separate choices.
- Minimize required keys. Movies use keyless **Wikidata**, TV uses keyless **TVmaze**. Do not reinstate TMDB as a default, required key or fallback. Optional exact-ID IMDb artwork already exists.
- Anime automatically discovers/rotates Sub/Dub providers through **EverythingMoe and The Index**. No static anime provider picker or seed-domain list. Dub-availability badge sources and non-anime manifests are separate dependencies.
- Optimize without weakening exact title/year/version/season/episode/audio matching. Unknown evidence stays unknown. Request IDs, provider labels and subtitles alone do not prove identity or spoken audio. Do not hide slow correct lookups behind shorter timeouts.
- Multiple players retain their own document/surface/position. Switching away pauses; switching back stays paused until explicit resume. Native controls overlay video, hide when idle/fullscreen, and reappear with pointer movement. Website playback has no duplicate app playback panel.
- TV/anime actions: **Stream episode / Watch via download / Download season**. Movie actions: **Stream movie / Watch via download / Download to library**.
- Temporary torrent episode downloads (e.g. Nyaa) remain until the **last owning player tab closes**, then delete. Permanent downloads survive.
- Books: prefer **Anna's Archive**, exact edition/file, working EPUB/PDF reader and saved progress.
- Arabic cartoons are last: old Arabic-dubbed anime such as **Flona/Heidi**, actual Arabic audio and correct episode order. Removal of Cartoons is undecided; do not remove it unilaterally.

## Current state and next action

**Recovery is incomplete.** Many core repairs exist; complete user journeys and release acceptance remain open. The next recorded action is:

1. Validate the actual **Movie/TV catalog → native player** path on the secondary monitor using the current build: correct item/unit, captions, seek, inactive pause/paused return and fullscreen.
2. Complete **source-change and restart resume** for the requested film and separate TV units; preserve identity across changed URLs/providers, with separate season/episode positions.
3. Continue the remaining agreed gates below. Anime is still not certified complete; recent Movie/TV samples do not waive its outstanding work.

Do not repeat earlier source extraction repairs merely because an older report failed. If a live path fails, capture the current failure and fix that concrete cause.

### Latest completed repair and evidence

The Movie/TV scraper now observes the provider's **original decoded item response**, including cross-site nested frames, instead of relying only on a redundant metadata lookup. It binds that response to the active API, release file and media URL before retaining identity/captions; independent title/ID/unit checks remain. Missing observations retain the existing fallback. No new key or persisted metadata cache was introduced.

Production progressive resolver/native-player samples passed for **Dune (2021), Breaking Bad S1E1 and S2E1**: independently matched items, decoded video, rendered English captions, seek, inactive pause, paused return and explicit resume. Handoffs were approximately **23.3 / 18.6 / 19.4 seconds**. These are network-dependent samples, not general accuracy or speed guarantees. Audio tags were absent, so language notices remain.

**These latest samples were muted memory-output service/player checks, not physical catalog-to-player UI tests.** They do not certify full-film captions, cuts, spoken audio, all providers or release readiness.

Last recorded baseline (not rerun for this handoff): **580 C# passes, 0 failures, 22 existing skips; 156 Python passes; build 0 warnings/errors.** Results and build/script hashes are in the latest QA report. Executable: `UniversalMediaOS.WPF\bin\Debug\net9.0-windows10.0.17763.0\UniversalMediaOS.WPF.exe`.

## Remaining work, grouped

Seven feature areas remain. The earlier checklist's 39 unchecked entries overlapped; the cleaned checklist groups that work by outcome. Neither count is a feature count or completion percentage.

| Area | Already implemented/observed | Still needed |
| --- | --- | --- |
| Anime/player completion | Retained player tabs, native overlay/idle controls, captions and named tracks; sampled Sub 1/2 and Frieren Dub; exact-match conflict guards, catalog aliases and bounded ID-backed synonym fallback; native-first lookup. | Broader Sub/Dub and caption/audio choices; titles without safe aliases/IDs; specials/unknown units; provider outage/cancellation and AniList catalog outage fallback; next/previous UI; provider-change/restart resume; playing Alt-Tab/minimize/restore, browser PiP, physical controls, DPI/RTL and resource settling. |
| Watch via download | Exact anime episode through Nyaa/MonoTorrent; real transfer/cancel and two-player MKV playback; Movie/TV direct-file temporary watches; shared leases and scoped cleanup/permanent-copy tests. | Visible button-to-player and last-tab cleanup; real Dub/sidecars, failures/disk limits; full item/audio correctness; HLS/DASH download support and TV season downloads. |
| Movies | Wikidata catalog; native nested-frame extraction, byte validation, progressive source cards, item/caption handoff and sampled native Dune. | Actual visible journey, broader providers/catalog/animated-film coverage, exact remake/cut/audio/full-duration captions, DASH segment validation, provider-change/restart resume and full watchability. |
| Television | TVmaze catalog/episode selectors; sampled independently matched native S1E1/S2E1 and captions. | Visible adjacent/two-season journeys, special mapping, separate resume across restart/provider changes, broader providers/audio and season downloads. |
| Books | Anna provider and EPUB/PDF readers exist. | Repair first-result edition matching and fabricated PublicDomain labels noted in the audit; exact hash/ISBN/edition/language/format, honest availability/access, real readable bytes, navigation/restart progress and progressive results. |
| Arabic cartoons | Transitional category/data retained. | Qualify Flona/Heidi-type aliases, actual Arabic audio, correct versions/episode order and shared-player behavior; defer until higher priorities. |
| Shared performance/services/release | Numerous isolated tests and startup checks pass. | Bound HLS request/session/body lifetimes and shutdown; progressive Book/Dub results and honest cancellation; bounded/coalesced image loading and grid virtualization; measured persistence contention/resume ordering; real migration/rollback; settings/accessibility/service repair/package checks; surrounding features and resource cycles; inventory 22 skips; final source/script/asset/native-dependency manifest and exact packaged executable with fresh/legacy-profile smoke tests. |

These are implementation and verification gaps, not proof that every preserved feature is broken. Work on a concrete user problem per batch; do not expand architecture just to check boxes.

## Practical continuation notes

- **Disk cleanup completed October 1:** repeated test/probe builds had grown the workspace to 58.677 GB. After the user explicitly requested execution, `tools/cleanup-old-builds.ps1 -Apply` removed 853 obsolete generated folders/branches with zero failures and reclaimed 51.405 GB of C: space. The workspace is now 7.453 GB (6.941 GiB), including 3.066 GB in `.artifacts`. Source, current project builds, media, profiles and QA evidence remain; current WPF/Core hashes are unchanged and native dependencies remain present. Results: `.artifacts/storage-audit-20261001/cleanup.json` and `after-summary.json`. Details: `docs/qa/storage-and-checklist-cleanup-2026-10-01.md`. Earlier deletion commands had been blocked; the later explicitly authorized script run succeeded. No further cleanup is needed for this batch. Reuse a single build location in future. The cleaned checklist has 23 grouped open tasks, preserving prior unfinished outcomes.

- Source resolution: `UniversalMediaOS.Core/audiovisual_scraper.py`, `OtherMedia/Audiovisual/`, `UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs`. Anime: `Core/scraper.py` and `Services/ScraperEngine.cs`.
- Playback: `WPF/ViewModels/PlaybackViewModel.cs`, `Views/PlaybackView.xaml[.cs]`, `Controls/PlaybackTabContentHost.cs`, `Core/Streaming/HlsLoopbackProxy.cs`, `Core/Services/MediaSubtitleTrack.cs`.
- Downloads: `Core/Archiving/TemporaryEpisodeWatchService.cs`, `SeasonDownloader.cs`, `DownloadQueueService.cs`. Books: `Core/book_scraper.py`, book providers/viewmodels/readers.
- Current Movie/TV resolution limits active crawlers to two, retains queued candidates until an independently matched native success, and keeps unknown usable candidates visible while better matches continue. Confirmed same-URL evidence upgrades the card. Native startup uses the validated HLS rendition.
- The latest frame observer uses DrissionPage **4.1.1.4** and its existing driver/CDP sessions. Cross-site targets require `Page.enable`, init-script installation and recursive attachment **before execution**, always resuming the target even if installation fails. A root-only init script previously missed the response. Preserve bounded copies and strict API/file/stream binding.
- Latest ignored artifacts: `.artifacts/implementation/movie-tv-item-readiness-20261001/`. Sanitized `loaded-native-results.json`, `native-progressive.log`, `enabled-metadata-blocked.log`, and the temporary native helper document the samples. Raw captures may contain signed URLs/full captions; do not publish them.
- C# result: `UniversalMediaOS.Tests.E2E/TestResults/movie-tv-item-readiness-2026-10-01.trx`; Python result: latest artifact folder's `python.log`.
- Desktop automation previously failed **before executing** with `failed to write kernel assets: The system cannot find the path specified. (os error 3)`. Read the current computer-use skill before trying supported desktop tools. If still unavailable, state the limitation and continue independent resume/code work. Do not repeat endless tooling retries or call offscreen checks physical UI acceptance. Do not improvise desktop input through shell scripts.
- `UniversalMediaOS.Tests.E2E/Infrastructure/AppFixture.cs` sets an isolated data root and `StartupMonitor=Secondary`. Use existing test infrastructure. Run checks appropriate to actual changes; don't repeatedly run the entire suite without a new reason.

Typical verification commands, from the workspace:

```powershell
dotnet build UniversalMediaOS.Tests.E2E/UniversalMediaOS.Tests.E2E.csproj --no-restore --verbosity minimal
dotnet test UniversalMediaOS.Tests.E2E/UniversalMediaOS.Tests.E2E.csproj --no-build --no-restore --logger "trx;LogFileName=<new-batch>.trx" --verbosity minimal
python -m unittest discover -s tools/tests -p "test_*.py"
```

Python 3.12 was available at `C:\Users\user\AppData\Local\Programs\Python\Python312\python.exe`; Node at `C:\Program Files\nodejs\node.exe`. Verify local availability rather than installing replacements unnecessarily.
