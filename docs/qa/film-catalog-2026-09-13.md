# Movie catalog repair and live app observations

September 13, 2026. IP05/IP06 focused Movie default change; overall app acceptance remains incomplete. Current work is tracked in the [separate checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md).

## Reproduced problem and change

The real `tvmaze-default-build` Movie page showed miscellaneous Archive uploads, including `test file mp4` and CapCut templates. A partial `big` search displayed zero matches on that page. This was not a disconnected Python scraper: discovery was using the general Archive metadata route, whose title filter also rejected partial matches.

Movie now uses the existing typed Wikidata film adapter regardless of saved TMDB keys or the Archive source toggle. It retains distinct provider IDs, years and real continuation. Optional IMDb artwork requires an exact IMDb ID, a film-type record and the allowed image host; it cannot replace the film's identity or year. Artwork has a four-second overall budget and failures preserve the film/page. Caller cancellation still cancels the operation. IMDb artwork is preferred over Commons redirects after a live app log showed a Commons poster returning 403; an explicit Wikidata poster remains a fallback.

Catalog/details show Wikidata attribution and available IMDb poster attribution. Settings explains the Movie/TV defaults and remaining Cartoon key. Opening feature details no longer starts/waits for source scrapers; Find sources is explicit. TV already followed this interaction. Existing library records are preserved; this batch does not introduce a data migration or change source verification.

## Live evidence and limits

| Check | Result |
| --- | --- |
| Isolated service startup | In `tvmaze-default-build` and the initial `film-catalog-build`, Python, HLS, FFmpeg and uBlock installation reached ready in about three seconds. qBittorrent was optional/missing. Actual WebView2 extension loading was not checked. |
| Desktop UI | Grouped media/utilities, Settings and populated anime results were observed. The later movie-build anime screen showed 108 records and service checks passed. Full buttons, DPI/RTL and playback acceptance were not performed. |
| Previous Movie page | General uploads and failed partial-title matching reproduced visually in the baseline app. |
| Actual MovieService, no mocks | Two browse pages and six searches returned typed films through the production HTTP factory after the maxlag fix. Dune 2021 and 1984 remained distinct. |
| Film metadata timing | Browse pages: 7.05 and 4.25 seconds; Dune 4.19, big 4.08, Matrix 5.22, Toy Story 4.13, Spider-Man 1.72, Your Name 4.53. These include optional artwork lookup, not WPF image rendering or source resolution. |
| Artwork follow-up | Final-source `big` probe returned five films/four poster URLs in 4.42 seconds. The Big Parade now uses its exact-ID IMDb poster; HEAD returned 200/image/jpeg. This verifies a response, not final-build image rendering. |
| Final desktop movie flow | Incomplete. Manual input and window minimization interrupted automation. The final poster-fix executable has passed tests, but was not certified through live Movies search/details/playback. |
| Playback/resume | Movie, selected TV episode, anime sub/dub, seek and reopen/resume were not verified in this batch. Source matching/evidence work remains open. |

The first live production-client probe returned no films because Wikidata sent HTTP 200 with a `maxlag` API error. Earlier Python probes alone missed this failure. The shared production HTTP client now identifies the app. Foreground catalog calls omit maxlag as permitted for interactive requests by [MediaWiki's maxlag documentation](https://www.mediawiki.org/wiki/Manual:Maxlag_parameter); batch qualification retains it. HTTP rate limits, coordinator backoff, public-network restrictions and response limits remain enforced. Sustained availability is not established.

Known discovery limitations: five-entity pages protect the existing 4 MB bound; browse is incoming-link order, not popularity; artwork may be absent; the direct class filter is incomplete; original audio remains unknown. The `Your Name` sample returned five other films but missed the intended animated film on its first page. That is a coverage/ranking issue to diagnose, not a passed exact-title test. Most sampled pages took more than four seconds. Cartoon's combined film/series discovery is still transitional.

## Verification and reproducibility

Final exact-build suite: **424 passed, 22 existing skips, zero failures**, with zero build warnings/errors. Fourteen new cases cover default routing under four legacy-setting combinations, exact-ID poster matching/rejection/fallback, artwork deadlines, cancellation, provider failure and explicit feature-source lookup. Earlier intermediate film runs had 422 passes before the final two poster cases. Skipped native journeys remain unvalidated.

Final artifact: [test executable](C:/Users/user/animeapp/.artifacts/implementation/film-catalog-final-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe). Test result: [TRX](C:/Users/user/animeapp/.artifacts/implementation/results/film-catalog-e2e.trx). Source/binary hashes: [manifest](C:/Users/user/animeapp/.artifacts/implementation/film-catalog-checkpoint.json).

Evidence is retained under the workspace:

- `.artifacts/implementation/live-recovery-20260913-182255/`: baseline launch manifest, real service log, Python qualification samples, failed production-service probe, and successful `film-service-fixed/film-service-sample.json`.
- `.artifacts/implementation/live-film-20260913-184239/`: movie-build launch/relaunch manifests, service/image logs and final `poster-fix-probe/` including `image-head.json`.
- `.artifacts/implementation/film-live-probe/`: small console probe calling the real MovieService/HTTP factory; output paths are isolated and no user keys are supplied.
- `.artifacts/implementation/film-catalog-before/`: pre-edit copies of existing changed files.

```powershell
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/film-catalog-final-build
dotnet run --project .artifacts/implementation/film-live-probe/probe.csproj --artifacts-path .artifacts/implementation/film-live-probe-build -- .artifacts/implementation/film-live-repeat big
```

The desktop evidence uses earlier executable paths explicitly named above. A passing test artifact is not evidence that the user currently has that same executable open.
