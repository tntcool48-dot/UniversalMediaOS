# Movie, TV, and cartoon subsystem audit — 2026-09-09

History follow-up: the original June tab design did hardcode three placeholder movies—Spirited Away, Your Name, and Akira—with playback unconnected. Those placeholders are absent from current source. This explains a historical three-card UI, but does not establish which executable the user saw. See the [recovered source and comparison](C:/Users/user/animeapp/docs/qa/git-and-task-history-comparison-2026-09-09.md).

The confirmed defect is an unsuitable, mixed Internet Archive discovery feed presented as separate Movie and TV catalogs. In the desktop run, Movies displayed **50 titles**, including `test file mp4`, `About Bananas`, `beat sync capcut template`, and many `electricsheep` clips. The root auditor then opened TV Shows and observed the same 50 titles, including the same first two items. **The reported limit of approximately three movies was not reproduced.** There is no hardcoded three-title limit in the audited catalog path. The code does limit requests to one page and can discard many records after retrieval, so very small result sets remain possible with different feed data or filters.

This report covers audiovisual metadata routing, identity and type classification, source resolution, Python extraction, catalog view models, and relevant tests. Production code was not edited. Live desktop observations were supplied by the root auditor from an isolated configuration with a blank TMDB key, default OtherMedia settings, and `AutoManageServices=false`. The sub-audit inspected the saved Movies observation and ran a deterministic, network-free Python scoring probe. It did not access secrets or establish that every current external provider is unavailable.

## Evidence and confidence

- **Live desktop:** [movie-catalog-observation.txt](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/movie-catalog-observation.txt:82) records `50 titles`; lines 88, 96, 144, and 272 onward show the examples above. The root auditor separately confirmed the matching TV feed during the same run.
- **Executed code probe:** actual Python scoring functions were extracted through the Python AST and executed without importing browser or network dependencies. Two wrong-sequel candidates were accepted; details below.
- **Source-confirmed:** routing, exact-title discovery filtering, identity loss, process fan-out, missing pagination, and failure-state handling follow directly from the code references below.
- **Not measured here:** live time to first playable movie, exact provider success rates, CPU/RAM cost per browser, the user's historical three-title result, and playback across a representative film sample. Timeout values below describe code limits and possible paths, not measured duration.

## Findings

### AV-01 · P1 · The default discovery feed does not represent a movie catalog

Production dependency injection supplies configuration to all three catalog services: [App.xaml.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml.cs:359). With a nonblank TMDB key, the metadata router selects TMDB. Otherwise it selects Internet Archive, or returns an empty client if Archive is disabled: [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:38).

Archive popular uses `mediatype:movies AND (licenseurl:* OR rights:*)`, sorts by downloads, and requests only `rows=50&page=1` (router lines 154 and 168–171). This is a broad video feed. It does not restrict discovery to identifiable films or television series. The desktop observation confirmed the practical outcome: miscellaneous clips and test uploads presented as movies.

The parser then discards records without accepted rights metadata (router line 233). It does not continue fetching to fill the result set. Thus the same implementation can return 50 unsuitable videos or a much smaller set, depending on the current page. The observed 50 should not be described as a successful film catalog.

An existing IMDb metadata client is reachable only through constructors where configuration is null: [AudiovisualCatalogServices.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogServices.cs:19). Production does not use that branch. The scraper contributes playback sources after a title is selected; it cannot populate missing metadata cards. Settings nevertheless says TMDB is optional and the scraper remains available: [SettingsView.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SettingsView.xaml:436). Disabling Archive with a blank TMDB key empties all three catalogs even though the Archive tooltip describes a source-waterfall option (line 420).

**Recommended correction:** provide a dependable discovery metadata path with identifiable films/series, independent of playback-source configuration. Add provider status and pagination. If retaining Archive discovery, use suitable collections/classification and fetch additional pages after filtering. Do not enable the legacy IMDb implementation without reviewing its own type filtering and brittle HTML parsing.

### AV-02 · P1 · Movie and TV types are assigned from the selected tab

All Archive tabs issue the same generic video query. The parser sets `Kind` to the requested tab and unconditionally considers every TV result a series: [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:248). It has no positive television check and does not exclude episodic records from Movies. The root auditor reproduced identical Movie and TV feeds live.

This also affects source selection. A Movie record containing season/episode fields is marked `Series`; the view model consequently requests an episodic unit: [AudiovisualCatalogViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs:164). The exact matcher, however, accepts only feature units for `Movie`: [ExactAudiovisualMatcher.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ExactAudiovisualMatcher.cs:82). Misclassified records can therefore be visible but structurally incompatible with resolution.

**Recommended correction:** derive type from provider metadata, preserve feature-versus-series identity, and exclude incompatible results before presenting them. Test Movie and TV queries against the same mixed fixture; both must not relabel the complete fixture as their own kind.

### AV-03 · P1 · Partial title search is rejected after discovery

Archive search issues a title-phrase query, then requires the result title to equal the entire search string after whitespace/case normalization: [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:140), lines 225–228 and 280–285. For example, searching `Living Dead` cannot retain `Night of the Living Dead`. Punctuation or suffix differences also fail the check.

**Recommended correction:** return ranked partial matches during discovery. Perform identity validation when mapping a selected title to a playback source. Add coverage for prefixes, partial titles, punctuation, alternate titles, and multiple adaptations.

### AV-04 · P1 · Wrong sequels can be accepted and labeled exact

Python `candidate_score` requires only up to two matching title tokens. A missing candidate year does not reject a candidate: [audiovisual_scraper.py](C:/Users/user/animeapp/UniversalMediaOS.Core/audiovisual_scraper.py:257). The search result schema retains no verified candidate year, ID, or episode. C# then assigns the requested identity and requested unit to the resolved source: [ScraperAudiovisualSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ScraperAudiovisualSourceProvider.cs:118).

The resolver exact-matches Archive and configured-provider sources, then appends scraper sources without applying that matcher: [AudiovisualSourceResolver.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualSourceResolver.cs:96). The UI calls the combined results exact sources: [AudiovisualCatalogViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs:527).

The following probe executed the real scoring functions extracted from the current Python file:

| Requested title | Requested year | Candidate context | Accepted |
| --- | --- | --- | --- |
| Dune Part One | 2021 | Dune Part Two movie | Yes |
| Spider Man No Way Home | 2021 | Spider Man Far From Home movie | Yes |
| Dune Part One | 2021 | Dune Part Two movie 2024 | No |

This demonstrates the missing-year loophole. It does not claim a particular external provider returned those fixtures during the live run.

**Recommended correction:** retain candidate evidence and provider IDs, validate the full identity and episode, and distinguish unverified candidates from exact matches. Do not overwrite candidate metadata with the request as a substitute for matching.

### AV-05 · P1 · Details lookup starts excessive work and withholds early sources

Opening details immediately starts source resolution: [AudiovisualCatalogViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs:465). The title-search path fans out to ten sites with five Python worker threads, a 14-second deadline, and up to 24 candidates: [audiovisual_scraper.py](C:/Users/user/animeapp/UniversalMediaOS.Core/audiovisual_scraper.py:365).

C# starts a separate resolver process for every distinct candidate without a concurrency limit and waits for `Task.WhenAll`: [ScraperAudiovisualSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ScraperAudiovisualSourceProvider.cs:61). Each resolver has a 45-second process timeout: [AudiovisualScraperEngine.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualScraperEngine.cs:84). Each Python resolver may launch its own Chromium instance, and each retries alternate providers that other candidates are already resolving: Python lines 593–619 and 673–688. The ID path generates four candidates, so the duplication also exists when TMDB/IMDb identity is available.

The combined resolver awaits scraper, configured-provider, and Archive completion before returning anything: [AudiovisualSourceResolver.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualSourceResolver.cs:92). The VM only populates source cards after that return. Even a fast successful source stays hidden while slow alternatives continue.

Archive lookup can independently retrieve twenty matching records and resolve their metadata sequentially: [InternetArchiveSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/InternetArchiveSourceProvider.cs:108), lines 140 and 218. Its per-request timeout is 15 seconds (line 73); there is no encompassing deadline for that whole loop. Initial Python dependency readiness occurs before the scraper process timeout starts: engine lines 111 and 122, so first-use initialization adds another unbounded-by-that-timer stage.

**Recommended correction:** surface sources as they arrive; limit Python/browser concurrency; resolve selected or promising candidates; deduplicate alternate-provider attempts across the operation; cache useful results; and use one cancellation/deadline budget that includes initialization. Measure time to first usable source as well as total lookup duration.

### AV-06 · P1/P2 · Archive cards cannot reliably resolve their own known item

Catalog parsing reads the Archive identifier but stores it only inside the poster URL: [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:223), lines 259–274. The identity model has TMDB and IMDb fields but no provider item identifier: [AudiovisualModels.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualModels.cs:55).

Source resolution therefore repeats a title/year search instead of resolving the already known Archive item. Catalog parsing permits missing years, which occurred in the desktop observation. The exact matcher rejects a missing-year identity unless both sides share a TMDB ID: [ExactAudiovisualMatcher.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ExactAudiovisualMatcher.cs:39). Such a card cannot match its own Archive source through this path. A further mismatch exists when catalog year is derived from `date` (router line 303), because the source query requires an explicit `year` field: [InternetArchiveSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/InternetArchiveSourceProvider.cs:132).

**Recommended correction:** preserve stable provider identity in catalog items and resolve known items directly. Keep strict matching for searches that genuinely require disambiguation.

### AV-07 · P2 · Audio filters assert the requested language without evidence

Scraper results receive the user's requested language, or English by default, without checking the media: [ScraperAudiovisualSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ScraperAudiovisualSourceProvider.cs:92). Movie/TV language filters therefore accept those sources regardless of actual audio language. The separate Arabic-cartoon verification path does not correct ordinary Movie/TV labeling.

**Recommended correction:** represent unknown language explicitly. Only declare a language from provider evidence or inspected player audio tracks.

### AV-08 · P2 · Provider failure and empty catalog are indistinguishable

HTTP errors, timeout, and provider backoff return null: [ProviderRequestCoordinator.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ProviderRequestCoordinator.cs:91), lines 126–136. Metadata clients convert null to an empty array, which the UI describes as `No matching titles`: [AudiovisualCatalogViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs:359). A nonblank but invalid TMDB key selects TMDB with no runtime fallback.

**Recommended correction:** separate empty results from unavailable providers, expose a clear status, and define a deliberate fallback policy. Preserve useful existing results during transient failure.

## Additional limitations

- TMDB search also fixes `page=1`: [TmdbMetadataClient.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/TmdbMetadataClient.cs:103). Its cartoon popular route filters animation after fetching a generic trending page, which can yield few items. The result deduplication uses only numeric TMDB ID (line 159), despite mixing movie and television namespaces.
- Audiovisual catalog cards use a nonvirtualized `ItemsControl`/`WrapPanel`: [AudiovisualCatalogView.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/AudiovisualCatalogView.xaml:215). Expanding discovery without addressing rendering will introduce another scaling cost.
- The existing IMDb client is not a complete fallback fix: its cartoon search accepts all movie/TV candidates without animation evidence, and its popular cartoon route reads ordinary TV HTML: [ImdbMetadataClient.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ImdbMetadataClient.cs:70), line 130.

## Test gaps and repair order

The existing Archive fallback test supplies one complete exact title and expects one result: [OtherMediaAudiovisualServiceTests.cs](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/Tests/OtherMediaAudiovisualServiceTests.cs:563). The test named `IndependentCatalogServices_DoNotCrossMediaKindsWithoutTmdb` disables Archive and expects empty catalogs (line 627); it cannot detect the live cross-kind defect.

Recommended order:

1. Repair default metadata discovery and film/TV classification; retain stable provider identity and implement pagination.
2. Correct partial search and sequel/episode identity checks before displaying sources as exact.
3. Bound resolver/browser concurrency, deduplicate provider work, and expose early results.
4. Correct language evidence, provider error states, and rendering scalability.
5. Validate with mixed-type fixtures, partial-title and sequel cases, missing-year Archive items, pagination, resolver-concurrency checks, and a representative live movie/TV playback sample. Record source availability and time to first usable source separately from total completion time.

No claim in this report establishes that all movies are unplayable. It identifies confirmed catalog defects and concrete resolution paths that can fail, select the wrong content, or make users wait unnecessarily.
