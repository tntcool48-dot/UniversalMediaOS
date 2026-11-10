# Movie/TV providers, second pass: browser profiles, release years, source runtime — November 10, 2026

Second pass on the open "other providers … exact remakes/cuts …" gate, on the merged `codex/app-recovery` (`401155b`). Branch **claude/movie-tv-providers-c8a6db**. Each fix has a check that failed first. Evidence, TRX files and the temporary real-app check sources are in `C:\Users\user\animeapp\.artifacts\implementation\movie-tv-providers-20261110\`. The earlier pass is in [the October 23 write-up](movie-provider-live-archive-2026-10-23.md).

**On the merged code first:** a clean Release build (zero warnings), then my October regressions re-run: **114 / 114**. These cover the Archive classes, auto-generated captions, profile cleanup, the scraper native path and caption handoff.

## 1. Lookups outlived the app and left browser profiles (`81f7103`)

download-captions reported that each Movies "Find sources" left a ~55 MB `umos_av_*` profile after a normal app close. That observation (October 23) predates the October 24 cleanup repair (`c7d5b09`). So I reproduced it on the merged code in the real app (isolated profile, desktop lock), with two close timings:

- **Closing during the search.** The app exited at 9.9 s, but its two scraper processes kept running on their own. They started Chromium after the app had gone (31.6 MB and 18.9 MB profiles 15 s later) and left two empty `umos_av_run_*` folders when they finished. The engine is an app-wide service, and `App.OnExit` disposes the service container. But the engine was not disposable, so nothing stopped its runs.
- **Closing after the search finished.** One run folder (an empty profile directory) remained with no process alive. A browser that is still exiting can hold its profile past the lookup's own 2 s cleanup. Once the run had ended, nothing tried again.

**Repair.**
- `AudiovisualScraperEngine` is now disposed with the app's services. It stops runs still in progress (kills each process tree, then waits briefly) and deletes every run folder it created and has not yet removed. It does this synchronously, so it does not depend on async continuations that may need the UI thread being shut down.
- A folder still held when its lookup ends is retried in the background (every 2 s, up to a minute) until it is released.
- Only the engine's own `umos_av_run_*` folders are touched.

| Run | Result | TRX |
| --- | --- | --- |
| New close case before the repair (the engine registered as the app does, then the container disposed) | **2 / 3**: "The lookup outlived the app" | `trx/scraper-close-before-20261110.trx` |
| New held-profile cases before their part of the repair | **3 / 4** (kept after close), then **4 / 5** (kept after release) | `trx/scraper-held-profile-before-20261110.trx`, `trx/scraper-held-release-before-20261110.trx` |
| After: profile cleanup, scraper native path, Unicode transport and Python preparation | **56 / 56**, run twice | `trx/scraper-profile-final-related-1-20261110.trx`, `…-2-…` |

**Real app after the repair.** In both close timings, no scraper process and no profile folder remained at exit, at +15 s or at +60 s. Before it, two orphaned scrapers and two leftover folders remained (`profile-check-before/`, `profile-check-final/`).

One cohort run hit a test-only race: a case read the fixture's `pid` file before the script had written it. That case now waits for the file, as the others do; it is not a product fault.

## 2. Release years the catalog entity records (`3376fe9`)

The Movies catalog showed only Wikidata's earliest release date, and matching compared that one year. *House on Haunted Hill* (Q263583) is recorded as released 1958-12-17, 1959-01-14, 1959-02-17 and 1959-10-27. The catalog showed 1958, so every Archive upload dated 1959 was rejected. A provider item for the same IMDb title dated 1959 was rejected as a year conflict, even though the IDs agree.

**Repair.**
- The catalog identity now also carries every year its own entity records (`ReleaseYears`); the shown year stays the earliest.
- Archive search asks for any of those years.
- Exact matching and evidence verification accept a year only when the catalog entity records it. Nothing is inferred from nearby years, and a remake from an unrecorded year still conflicts.
- The catalog shares one array per distinct set of years, so two reads of the same film stay equal records. An existing catalog test checks this.

| Run | Result | TRX |
| --- | --- | --- |
| New `CatalogReleaseYearTests` before the repair | **3 failed / 4 guards passed**: years not recorded, the 1959 upload not offered, the 1959 provider item rejected | `trx/catalog-release-years-before-20261110.trx` |
| After: the 25 identity, catalog and provider classes | **415 / 415** | `trx/catalog-release-years-related-20261110.trx` |

The first cohort run failed `FilmCatalogRecoveryTests` four times. Two reads of the same film were no longer equal records, because each read built a new year array. Sharing the array fixed it.

**Live** (one Wikidata search plus the Archive resolver, `release-year-live.txt`):
- The catalog's *House on Haunted Hill* records `[1958, 1959]` and now finds **7** Archive uploads, all dated 1959. It found 0 before.
- The 1999 remake's entity records its own `[1999, 2000]` and does not cross.

## 3. A source's own runtime on its card (`a06091a`)

Archive uploads of *The Lost World* (1925) run 68, 76, 92 and 104 minutes, all titled and dated as the film. Nothing on a source card told them apart.

**Repair.** A source now carries its own declared runtime, an Archive file's `length` (`DurationSeconds`), and its card shows it ("Runtime 1 h 8 min"). It is display only: no threshold, and never identity evidence. Sources that declare no runtime (Vidsrc, website pages) show no line.

| Run | Result | TRX |
| --- | --- | --- |
| New `SourceRuntimeCardTests` before the repair | **6 failed / 1 passed**. The provider set no runtime, the card had no text, and the production card template (rendered off-screen) showed no runtime line. | `trx/source-runtime-before-20261110.trx` |
| After: the 19 source, card and catalog classes | **363 / 363** | `trx/source-runtime-related-20261110.trx` |

**Real app** (read-only UI Automation, isolated profile, lock held). *The Lost World* (1925) → Find sources finished with "0 verified, 14 unverified candidates: 11 native stream options, 3 website fallbacks". The ten Archive cards showed **Runtime 1 h 8 min** (four, the abridged print), **1 h 16 min**, **1 h 32 min** (four) and **1 h 44 min**. The catalog's runtime is 104 min. Vidsrc and website rows showed none (`runtime-cards-live.txt`).

## Still open

- **Catalog runtime on the card.** The card shows the source's runtime only; the catalog's own runtime (Wikidata P2047) is not yet displayed beside it, so comparing needs the film's length known. Cut labels ("abridged", "restored") are not inferred.
- **Spoken audio:** no provider declares it, so a listener is needed.
- **Captions:**
  - human Archive subtitles with no declared source file;
  - coverage of Vidsrc captions against the runtime (next pass).
- **Archive TV episodes:** next pass.
- **Configured providers:** no index exists, as the user confirmed.
- **Catalog DASH.**
- **Crashes and forced exits** still skip the engine's disposal, so their run folders are not removed. A normal close, cancellation, timeouts and failures are covered.
