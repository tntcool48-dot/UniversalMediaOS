# Non-TMDB keyless metadata qualification

Updated: September 13, 2026. Status: **TVmaze selected for Television; typed Wikidata with optional exact-ID IMDb artwork selected as the limited Movie default; combined Cartoon strategy remains open**. Earlier dated checkpoints below record qualification history, not the current selection.

The target is useful movie, series and animation discovery without a user key or TMDB dependency. A working search endpoint alone does not satisfy IP05. Movie and Television have keyless non-TMDB defaults; Cartoon discovery remains transitional. Film coverage, latency and live playback are still acceptance gates.

## Initial three candidates (September 10)

| Candidate | Evidence and fit | Decision at this checkpoint |
| --- | --- | --- |
| IMDb | The existing suggestion endpoint returned title records for Matrix. Its local `GetPopularAsync` implementation actually scrapes TMDB HTML. The documented non-commercial datasets offer an alternative structured route, with daily updates and title types. | Reject the existing client as the replacement. Dataset download/index/update costs and applicable distribution terms must be evaluated before selecting an offline implementation. No bulk dataset was downloaded. |
| TVmaze | Keyless search, show index and episode requests succeeded locally. The C# qualification adapter preserves distinct remake IDs and filters cartoon results by animation type. | Useful series candidate; not a film replacement. Search adapter implemented behind IP04 contracts, not enabled in production DI. |
| Wikidata | Keyless search plus one batched entity request per query returned film IDs and typed claims for the fixed movie sample. Raw name search also returned games, albums, people and franchises. | Promising film identity candidate; browse, classification coverage and presentation quality remain unqualified. Do not enable raw name search as a movie feed. |

Primary documentation: [IMDb datasets](https://www.imdb.com/interfaces/), [TVmaze public API](https://www.tvmaze.com/api), [Wikidata data access](https://www.wikidata.org/wiki/Help:Data_access), [Wikidata query limits](https://www.wikidata.org/wiki/Wikidata:SPARQL_query_service/query_limits).

TVmaze documents a show index of up to 250 records per page, ending on HTTP 404; short pages can occur before the end. It is an index, not a popularity ranking. Search results can be sliced locally but do not have upstream search paging. Public API data requires attribution and ShareAlike compliance under its documented license; retain source attribution before shipping UI integration. The adapter uses the existing request coordinator with 60 requests/minute, a 12-second request timeout, a 4 MB response limit and a one-hour cache.

Wikidata documents CC0 data and recommends an identifying User-Agent and respecting rate limits. Broad SPARQL discovery must be measured against its service limits. This checkpoint uses bounded Action API requests instead. Poster/image rights remain separate from entity data.

## Fixed live sample and observed gaps

Evidence: `.artifacts/implementation/metadata-qualification/qualification-sample.json`. The sample runs without credentials, cookies or a browser. Results describe metadata, not playable source availability.

| Sample | Observed result |
| --- | --- |
| The Matrix | Wikidata returned film Q83495 / tt0133093 and the Resurrections sequel, alongside non-film entities. |
| Dune | The first eight name matches included the 2021 film Q60834962 / tt1160419, but not the 1984 film. This fails remake coverage at the tested depth. |
| Toy Story | Q171048 / tt0114709 had typed film evidence but lacked an English label in the returned entity payload. A title/label fallback must be verified, not guessed. |
| Toy Story 2 | Film Q187266 / tt0120363 was distinct from same-name games and an album. Animation uses a different instance type from generic film, requiring explicit classification support. |
| The Office | TVmaze returned ten matches, including separate 2005 and 2001 versions with IDs 526 and 1292. |
| Avatar | TVmaze returned animated and other series. Fixture tests retain only independently typed animation for Cartoon search. |
| Series browse | TVmaze index pages 0 and 1 returned 240 and 245 entries. A short-page-is-end assumption would lose results. |
| Episodes | TVmaze show 526 returned 202 regular episode entries with season/episode numbers. Specials and app episode API integration remain untested. |

The initial seven endpoint probes returned HTTP 200 in roughly 0.3–0.6 seconds each on this host. A subsequent Wikidata request without an identifying User-Agent returned 403; the bounded tool with that header completed the fixed sample. These are single-run observations, not availability or app latency guarantees. The movie sample had no poster property P3383; alternative artwork coverage is still pending.

## Implementation and reproducibility

`TvmazeMetadataClient` implements search and local continuation through the existing metadata interface. It rejects Movies, unqualified discovery and locale overrides with typed Unsupported outcomes. It preserves IDs, years and upstream relevance order; does not assign TMDB IDs, invent original-language metadata or claim dub availability. Malformed data and rate limits remain distinguishable from a successful empty search.

Eight deterministic tests use trimmed, embedded TVmaze fixtures plus failure responses. Full isolated build: zero warnings/errors; **306 passed, 22 existing skips, zero failures**. No production provider selection changed.

```powershell
python tools/qualify-keyless-metadata.py --output .artifacts/implementation/metadata-qualification
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/metadata-build
```

## Second qualification checkpoint: bounded typed film adapter

September 10, 2026: implemented `WikidataMetadataClient` through the IP04 interface. It uses documented [typed statement search](https://www.mediawiki.org/wiki/Help:Extension:WikibaseCirrusSearch) with the pipe syntax for alternative film classes and [search continuation](https://www.mediawiki.org/wiki/API:Search). Each app page requests at most five entities in one batch, with the existing response limit and timeout. A 20-entity probe exceeded 4 MB; the bounded browse samples completed within that limit.

Typed Dune search now includes the 1984 and 2021 films, plus Part Two. The adapter reads `en`, then the returned `mul` label; the recorded Toy Story fixture now produces its title without guessing. Two browse pages yielded different film IDs. Browse uses incoming-link ranking, not a claimed popularity or trending metric. This is a limited direct-class prototype, not complete Wikidata subclass coverage.

The parser validates returned identity and non-deprecated film/animation claims, rejects conflicting IMDb projections, and leaves original language/audio unknown. It uses only an explicit poster property for artwork. Unknown or missing entities are reported as partial results. API errors, cancellation and invalid continuation remain distinct from a successful empty catalog; a failed entity batch does not advance the page.

Validation: ten new deterministic cases; full isolated build **316 passed, 22 existing skips, zero failures**, with zero build warnings/errors.

New evidence: `.artifacts/implementation/metadata-qualification/typed-film-sample.json` and trimmed embedded `wikidata-*-search/entities.json` fixtures. The successful browse and Dune probe pairs took about 1.6–3.8 seconds on this host. The repeated Toy Story request returned HTTP 403; its earlier successful response remains the recorded fixture. No live-availability guarantee or production switch follows from these results.

Reproduce the additional sample with:

```powershell
python tools/qualify-keyless-metadata.py --typed-films --output .artifacts/implementation/metadata-qualification
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/wikidata-build
```

## Third qualification checkpoint: series index and episode API

September 10, 2026: TVmaze now supports its zero-based show index through the paged metadata contract. The adapter slices an upstream page of up to 250 shows without losing entries after offset 100. Short pages retain continuation. Only an index 404 terminates browsing; HTTP 404 is now a typed NotFound result in the coordinator and does not impose provider-wide outage backoff. Other callers still receive NotFound, not empty success.

Cartoon browsing checks at most three index pages per call when animation filtering leaves a page empty. It returns continuation after that budget so callers can continue without an unbounded crawl. This supplies an index, not a popularity ranking or a finished discovery UI.

Added `IAudiovisualEpisodeMetadataClient.GetUnitsAsync(identity, season, token)` and typed episode results, forwarded through the metadata router and Movie/TV/Cartoon service contracts. TVmaze requires an unambiguous series ID, reads `episodes?specials=1`, filters seasons, preserves separate episode IDs, and rejects malformed or conflicting numbering. Specials preserve provider IDs with null playback units; unknown regular numbering is partial metadata. Legacy providers report Unsupported rather than an empty season. No source audio or playback availability is inferred from episode metadata.

The captured show-526 response contains 203 entries, including the season-nine Retrospective special with no episode number. Deterministic tests cover that payload, S1E1/S2E1 separation, specials, index slicing/termination, bounded animation refill, missing-resource isolation, router forwarding, invalid data and cancellation. Fixtures contain only the necessary fields; attribution is recorded alongside them.

Validation for the series checkpoint: zero build warnings/errors; **327 passed, 22 existing skips, zero failures** in the full exact-build suite.

## September 13 decision: enable Television independently

Select TVmaze as the keyless default for Television browsing, search and episode metadata. The app routes TV requests there regardless of saved TMDB credentials or the Archive toggle. Per-catalog capabilities expose no locale support; they do expose series episodes. Existing TVmaze show IDs also own saved-series episode lookup across catalog categories. A saved TMDB-only identity is preserved and receives Unsupported episode metadata, with explicit manual entry in the UI; no title-based mapping is inferred.

The expanded live sample was repeated without credentials: The Office (10 results, 9 posters), Breaking Bad (8/8), Dark (10/10), Severance (3/3), Avatar (10/10), and One Piece (3/3). The Office remakes retain distinct provider IDs/years. Index pages returned 240 and 245 shows with posters for all entries, and show 526 returned 203 episode records. Individual requests took approximately 0.22–0.59 seconds across the two runs on this host. These are observations, not sustained availability or end-to-end app latency guarantees.

UI adoption includes source links to TVmaze on the catalog/details, plain-text summaries, ratings and named season/episode choices. Browsing is labelled as an index rather than a popularity ranking. Details do not start series scraping automatically; source lookup follows an explicit episode selection/action. Specials without numbered units remain visible but cannot be matched as guessed S0E1/S1E1. Error/unsupported episode metadata leaves manual-entry and retry controls available. No audio or subtitle availability is inferred from show metadata.

The [TVmaze API documentation](https://www.tvmaze.com/api) describes public access, the index, episodes, images and CC BY-SA attribution requirements. The UI links directly to the show page derived from its validated ID, or the TVmaze site on the catalog. Images/metadata retain their upstream provenance; no locally guessed poster is substituted.

Cinemeta was also sampled through the official metadata add-on endpoint linked by [Stremio's official add-on list](https://github.com/Stremio/stremio-official-addons/blob/master/index.json). Movie browse, Dune search and Matrix details returned useful IMDb identities; the browse sample contained `moviedb_id` and `popularities.moviedb` fields. This is evidence of mixed upstream metadata and does not establish a clean non-TMDB replacement. Cinemeta is not enabled. No extra API key or metadata subscription was added.

Evidence: `.artifacts/implementation/catalog-selection-20260913/tvmaze-selection.json`, its `repeat/` sample, and the `manifest.json`, `movies.json`, `dune.json`, `movie.json` candidate payloads in that directory. Reproduce TV sampling with `python tools/qualify-tvmaze-default.py --output <directory>`. Deterministic default-routing and UI tests complement the earlier adapter fixtures. The final verification totals and visual artifacts are in [the checkpoint QA](C:/Users/user/animeapp/docs/qa/tvmaze-default-2026-09-13.md).

## September 13 Movie decision after live app reproduction

The existing default Movie route showed general Archive uploads, including test videos, and discarded partial-title matches. Select the bounded typed Wikidata adapter for Movies to remove that confirmed failure. This is a limited recovery default, with the remaining coverage/performance gates below; it is not a claim that the film strategy is fully qualified. Reuse existing identities, continuation and request coordination. Movie routing is independent of saved TMDB keys and the Archive playback-source toggle; Cartoon keeps its separate transitional route.

Artwork comes from an exact IMDb-ID suggestion request, requiring a single matching film record and the expected HTTPS image host. Title-only poster matching is not used. Prefer that artwork after an explicit Commons poster returned 403 in the live app; retain explicit Wikidata artwork if optional lookup fails. Poster lookup has a four-second overall budget. The IMDb suggestion endpoint is an observed public endpoint, not a documented availability guarantee; artwork terms and distribution suitability still need release review. No user key, login or dataset download was introduced.

The actual production HTTP factory initially returned Wikidata maxlag errors despite successful earlier Python sampling. It now sets an identifying User-Agent; foreground requests omit maxlag under the official [interactive-request guidance](https://www.mediawiki.org/wiki/Manual:Maxlag_parameter). Batch tools retain maxlag and HTTP rate limits/backoff remain enforced. The repeat MovieService sample returned typed films for two browse pages and six searches, including Dune remakes, in 1.72–7.05 seconds per page. A final `big` query returned five films and four posters; the previously failed Big Parade poster resolved by exact IMDb ID and returned HTTP 200.

These are metadata/artwork observations. The first five `Your Name` results missed the intended animated film; direct-class coverage/relevance needs work. Five-item pages, incomplete posters, provider availability, final desktop rendering and playback remain open. Evidence and exact test-build totals are in [the Movie QA record](C:/Users/user/animeapp/docs/qa/film-catalog-2026-09-13.md).

## Remaining selection gates (updated September 13)

Catalog pages, provider identity and the versioned IP08 library are implemented. TVmaze supplies the TV catalog/episode picker and Wikidata supplies the limited Movie catalog. The film acceptance gaps and combined Cartoon strategy must be resolved before IP05/IP06 can close.

- Broaden film browse/partial-title and subclass coverage beyond the tested direct classes, and qualify artwork and sustained availability. Both Dune versions and shared-language label fallback now pass recorded adapter tests. Assess browse relevance and page latency before preferring this strategy over a measured IMDb dataset index.
- Complete alternate episode orders, exact special playback mapping, independent source identity/audio verification and live playback acceptance. Numbered episode selection and request cancellation are implemented; a metadata list alone does not establish playable availability.
- Complete classification, locale capability, cancellation and failure fixtures for the remaining combined film/animation strategy. Maintain attribution and continue measuring provider availability.
- Complete the film acceptance evidence and select/migrate Cartoon's combined catalog under IP06. The Movie default fixes general-upload discovery but does not complete IP05 or prove correct playback.
