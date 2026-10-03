# Automatic anime discovery and live recovery checks

## Scope and provider history

The user's intended anime flow is automatic discovery from EverythingMoe (called AnimeMoe in the request) and The Index, followed by provider rotation for the chosen episode and Sub/Dub preference. No provider API key or static provider dropdown is needed.

Git HEAD already contained a `Scraping Provider` selector; recovery work added its Automatic entry. The selector read saved `CustomSources`, not the live indexes, and its choice did not control the first automatic crawl. Python also appended three fixed seed domains. This was inconsistent with the requested behavior.

This batch removes the anime provider selector and the Settings manual-provider editor, stops seeding new profiles with fixed providers, and always routes normal anime Watch actions through the indexes. Existing saved custom URLs are preserved when unrelated settings are saved. Player Open URL and legacy explicit-URL APIs remain separate.

The scraper now uses EverythingMoe's current `/section/anime` endpoint and The Index. It caches only acceptable entries attributed to these indexes, replaces stale domains even when a refresh yields one site, and uses the last discovered list during an outage. No static seeds are appended. A cold outage reports no indexed sites without opening a browser.

Dub-availability badges still use their separate configured providers, including AniKoto. Movie/TV/Cartoon scraper seeds and manifests have not been migrated by this anime change. These are remaining fixed-provider dependencies, not part of the restored playback picker.

## Wrong-title regression

The September 15 live app requested **Mushoku Tensei: Jobless Reincarnation Season 3, episode 1, Dub**, but selected Miruro's **Attack on Titan** watch page. Playback was stopped immediately; no correct video playback was established.

The browser-anchor parser added a watch-path bonus after the title scorer returned zero for a rejected title. That made unrelated recommendation cards eligible again. The parser now discards rejected scores before applying route bonuses. A fixture based on the observed URL returns one wrong candidate with the saved previous source and zero with the repaired source; matching and opaque-ID cards remain eligible when their own labels supply title evidence.

This fixes that demonstrated rejection bypass. The same live attempt also tried a Mushoku **Part 2** candidate after the Season 3 candidate failed. Exact season/part matching, independent episode/audio evidence and final player-page validation remain open. The six matching regressions do not establish comprehensive identity verification.

## Automated and network evidence

- Latest build: `.artifacts/implementation/player-close-build`; zero warnings/errors, **432 passed, 22 existing skips, zero failures**. The earlier index-only build passed 429 cases before the live close crash was discovered.
- Python: `python -m unittest discover -s tools/tests -p 'test_anime_*.py' -v`; **16 passed**. Ten index/cache/outage/rotation tests and six wrong-title tests. These use controlled fixtures.
- Five added C# cases cover automatic sub/dub routing in native/browser modes and fresh/saved provider configuration behavior. Three additional cases verify that queued callbacks are dropped before/after disposal and active players still receive updates.
- Live index-only probe returned **203 deduplicated entries in 0.39 seconds** on September 13. The September 15 in-app attempt again discovered 203 entries. This is a discovered pool, not a count of working playback services.
- Exact source/build hashes are in [the checkpoint manifest](C:/Users/user/animeapp/.artifacts/implementation/index-discovery-checkpoint.json). [Final TRX](C:/Users/user/animeapp/.artifacts/implementation/results/player-close-e2e.trx), [index probe](C:/Users/user/animeapp/.artifacts/implementation/index-discovery-live/index-report.json), [before/after wrong-title result](C:/Users/user/animeapp/.artifacts/implementation/index-discovery-live/wrong-title-before-after.json).

## Live desktop evidence

Tests used real services and the isolated profile `.artifacts/implementation/index-discovery-manual-20260913-231032`. Launch manifests and its `Roaming/UniversalMediaOS/app.log` identify builds and times; screenshots were inspected through Computer Use in this task.

The initial September 13 launch supplied JSON booleans to a string-valued configuration and was corrected before reuse. That was a test-launch input error. The first uBlock download timed out; September 15 startup subsequently passed all required health checks, and the first browser fallback logged **Loaded WebView2 extension: uBlock Origin**. Optional qBittorrent remained absent. The historical staging access-denied issue was not reproduced.

The first September 15 app (`index-discovery-build`) showed automatic-discovery instructions without a provider dropdown, working Sub/Dub controls, episode availability and the playback-mode dialog. It exposed the wrong-title failure above. The subsequent build (`index-discovery-final-build`, launch manifest `final-launch-20260915.json`) includes the matching repair and reached required service readiness in about two seconds on this already-prepared profile.

Movie checks in the matching build, followed by the corrected player build, observed:

- Browse returned five real films, four with visible posters; one had an artwork placeholder.
- Dune search displayed separate **Dune (2021)**, **Dune (1984)** and **Dune: Part Two (2024)** cards with distinct posters, plus The Dune and Jodorowsky's Dune.
- Load more increased the catalog from five to ten titles while retaining the first page.
- Selecting Dune (1984) displayed the correct year, David Lynch description, poster and Wikidata/IMDb attribution. Details waited for explicit **Find sources**.
- Source search eventually displayed five candidates, including Internet Archive and browser routes. It was still busy at 38 seconds and complete by the observation at 95 seconds; the exact completion instant was not recorded. The UI's “exact sources” and audio labels use the legacy path and must not be treated as independently verified claims. A background provider page briefly occluded the app during resolution; its provenance was not independently established.
- The first Archive source started native video. The visible David Lynch credit and opening scene support identification as his Dune film; the exact cut and audible language were not verified. Runtime shown was 3:03:04. +30-second seek and keyboard pause worked. Logs saved 56.1 seconds under `av:wikidata:item:Q114819` / `feature`.
- The first player-tab closure at 03:24:13 crashed the process. Windows recorded `0xc0000005`, with `LibVLCAudioGetTrackDescription` called by `RefreshAudioTrackState` / `RefreshCaptionState` in a queued dispatcher callback after native disposal. [Windows crash evidence](C:/Users/user/animeapp/.artifacts/implementation/player-close-before/windows-crash-events.json).
- The repaired view model marks disposal as started before native teardown, rejects new and queued dispatcher callbacks, and guards direct track refresh/native event entry points. Final resume saving still runs before releasing the native player. The tested executable is `player-close-build`, identified by `close-fix-launch-20260915.json`; it supersedes both earlier builds in this batch.
- In `player-close-build`, the saved movie reappeared in My Library after restart. At 12:57:35 the app restored **56.1 seconds**, displayed advancing video and survived tab closure. Reopening in the same session restored **72.7 seconds**; the visible Pause button worked, progress reached **99.8 seconds**, and closing while paused also left the app responsive. No whole release acceptance follows from these two closure checks.
- The details summary initially continued to show the old last-opened time until reopening the saved entry. Native-video transitions also briefly obscured controls during capture; sustained rendering behavior and keyboard focus require broader checks.
- Final anime retry used the same Season 3 / episode 1 / Dub selection and 203 index entries. It rotated from AniKoto to Miruro and selected `/watch/178789/mushoku-tensei-iii-isekai-ittara-honki-dasu`, matching the requested AniList ID and visible episode 1 “Burn Bright, Mad Dog.” The wrong Attack on Titan recommendation was no longer selected. Resolution took about 43 seconds (13:00:05–13:00:48).
- The candidate media response was rejected as JPEG and the resolver supplied a browser fallback. That page loaded, including episode cards and uBlock, but did not start video. At 13:01:19 the app reported its 30-second browser-playback timeout. Play-control attempts did not establish video or the requested Dub track. This remains a failed playback check, not evidence that the whole service is unavailable.
- Closing the failed browser tab succeeded and the app stayed responsive. Anime details still labeled the resolver “Ready” after downstream playback failed; separating discovered-page status from playback success is an additional visible follow-up. Windows Application logs contained no new UniversalMediaOS crash events after launching the corrected build.

## Remaining acceptance

Dune native video, seek, pause, saved-position restoration and two player closures have now been observed. Exact cut/audio, changed-provider resume, complete anime/TV episode playback and repeated lifecycle acceptance remain unverified. Broader movie relevance/classification and latency, exact anime season/part matching, truthful source/audio evidence, Cartoon routing and packaged-profile acceptance remain open. Keep these in the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md), rather than treating passing internal tests as a completed app.
