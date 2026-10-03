# Universal Media OS codebase audit

Implementation handoff: see the [current recovery and improvement plan](C:/Users/user/animeapp/docs/APP_RECOVERY_PLAN.md) for prioritized repairs, dependencies, preservation requirements, and release acceptance checks.

Audit started September 8 and completed September 9, 2026, Asia/Amman.

The reported problems have concrete causes. The shell groups every destination into one left-aligned wrapping strip, shared control templates impose conflicting sizes, and movie discovery and source verification are not consistent with each other. Several loading paths wait for expensive duplicate work before showing useful results.

The appropriate next step is a focused repair of these paths. A further broad visual rewrite would leave the catalog and loading defects intact.

## Scope and evidence

Reviewed the three-project .NET/WPF solution, its three Python scrapers, service registration, shared themes, navigation, search, movie/TV/cartoon discovery and playback resolution, dependency initialization, image loading, book aggregation, persistence/lifecycle paths, and test infrastructure. The source inventory contains 158 C# files, 21 XAML files, and three Python files, excluding build output. Review depth was greatest in the reported problem areas; this is not a claim that every line or every external provider was exhaustively verified.

The checkout already contained extensive modified and untracked work. No production code was changed during this audit. Git alone cannot reliably attribute the accumulated edits to a particular model. The subsequent [Git and task-history comparison](C:/Users/user/animeapp/docs/qa/git-and-task-history-comparison-2026-09-09.md) recovers the original tabbed layout, the historical three-movie placeholders, and specific patches contributing to current defects. Earlier assistant claims were not treated as current verification.

| Check | Result and limits |
|---|---|
| Build | `dotnet build UniversalMediaOS.sln -c Debug --no-restore --nologo` succeeded: zero warnings, zero errors. An initial sandbox SDK-access failure was environmental; the subsequent build succeeded. |
| Existing suite | 264 total: **242 passed, 22 skipped, zero failed**, reported duration 29 seconds. Includes service tests and the existing isolated desktop checks. [TRX result](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/audit-2026-09-08.trx). |
| Python syntax | All three scraper files parsed successfully with Python's AST parser. Syntax validity does not establish provider compatibility. |
| Shared WPF controls | Actual resource dictionaries were loaded and measured. A nominal 72×34 DIP playback selector measures 220×38 DIP. Its visible inner border measures about 62 DIP wide. [Probe image](C:/Users/user/animeapp/tmp/ui-audit-control-probe.png). |
| Running desktop app | A freshly built app was inspected using a separate audit profile. Confirmed crowded navigation, small inset dropdowns, white disabled tab arrows, and separate settings navigation/content columns. Automatic service setup and background prefetch were disabled in this profile. |
| Live movie discovery | With no TMDB key, Movies returned **50 mixed Archive uploads**, including `test file mp4`, `About Bananas`, editing templates, and generated-art clips. TV Shows returned the same 50 titles. [Movie observation](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/movie-catalog-observation.txt), [TV observation](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/tv-catalog-observation.txt). |
| Movie identity probe | The repository's scoring functions accepted two wrong-sequel fixtures. No browser or external request was involved. [Runnable probe](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/probe_movie_identity.py), [results](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/movie-identity-probe.json). |

The exact reported count of three movies was **not reproduced in the current build**. Historical task evidence does contain exactly three placeholder movies: Spirited Away, Your Name, and Akira. Those placeholders are absent from current production source; they could explain an older build's behavior, but that was not established. The current default feed is limited to one page and can shrink after filtering, while the live problem observed here was primarily poor catalog relevance and incorrect media classification. A complete live movie playback session was not validated. The latency figures below are configured budgets and reachable code paths, not stopwatch measurements of the user's sessions or a CPU profile.

## UI findings

**UI-1 · P2 — Navigation grouping was flattened.** All 12 destinations, including Settings, share the same default-left `WrapPanel` in [MainWindow.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml:156). The previous committed shell separated navigation/system groups. Isolated measurement at 1280 DIP places the buttons approximately between x=8 and x=910, leaving substantial unused space on the right. At the supported minimum width of 900 DIP, Settings wraps onto a row by itself. The running app corroborates the left-heavy grouping.

Restore a deliberate layout: grouped media destinations, a separate utility area, and a stable Settings position. The recovered June tabbed shell already used media on the left, a flexible spacer, and utilities/Settings on the right; this is the appropriate design reference. The July 18 frontend cleanup replaced that grouping with wrapping navigation. Define how navigation behaves at narrow widths instead of letting a generic wrapping panel decide. The settings page itself still has two columns; its controls have not all accidentally been assigned to one cell.

**UI-2 · P2 — Compact dropdowns inherit oversized global constraints.** [Controls.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Themes/Controls.xaml:175) gives every ComboBox `MinWidth=220`. This overrides the playback toolbar's requested 72/88 DIP selector widths. A movie language selector is placed in a 170 DIP parent, which cannot contain that minimum. Remove the global width requirement and specify sizes according to each control's context.

**UI-3 · P2 — The dropdown template draws a tiny pill inside a large control.** Its nested ToggleButton uses native centered content rather than a full-size template: [Controls.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Themes/Controls.xaml:210). The measured visible border is about 62 DIP within a 220 DIP control. This explains both odd proportions and apparently wasted spacing. Fix the template and its alignment as well as the minimum width.

**UI-4 · P2 — Tab action buttons retain native disabled rendering.** The reorder controls have no custom template at [MainWindow.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml:245). The running app displays white rectangles behind disabled arrows on otherwise dark tabs. A shared tab-icon template needs explicit normal, hover, focused, and disabled states.

**UI-5 · P2 — Hover and light-theme colors conflict.** The shared hover background is hardcoded dark while a primary button retains its dark foreground: [Controls.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Themes/Controls.xaml:50). Their computed contrast is approximately 1.50:1. A secondary button in light mode has a similarly poor pairing, approximately 1.47:1. Define background/foreground pairs per theme and button state.

Measurements, exact affected controls, and validation recommendations are in the [detailed UI audit](C:/Users/user/animeapp/docs/qa/ui-audit-2026-09-09.md).

## Movie and TV findings

**MOV-1 · P1 — Discovery defaults to a generic Archive feed.** Production service registration supplies configuration, and [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:38) selects TMDB only when a key is present. Otherwise it selects Archive, or an empty client if Archive is disabled. The legacy IMDb fallback is not on this production path. The scraper resolves sources only after a catalog item exists; it cannot fill gaps in discovery.

The Archive query loads page one with 50 records, then filters locally: [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:164). It neither retrieves replacements nor exposes pagination. Live output included test uploads and templates labeled as films. Establish a usable metadata catalog separately from playback sources, with explicit provider status and pagination. Simply disabling authorization checks or indiscriminately returning more uploads would not produce a film catalog.

**MOV-2 · P1 — Movies and TV are not classified independently.** The same feed is used for both tabs, and parsing assigns the requested tab's media kind. TV results are unconditionally considered series: [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:248). The same 50 titles appeared in both tabs during the live check. Movie entries carrying episode fields can also become internally inconsistent with feature-only matching. Preserve reliable content-type metadata instead of inferring it from where the user clicked.

**MOV-3 · P1 — Discovery rejects ordinary partial searches.** After an Archive title search, parsing requires the complete returned title to equal the query: [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:226). `Living Dead` therefore rejects `Night of the Living Dead`. Discovery needs ranked partial matches; exact identity checks belong at source selection.

**MOV-4 · P1 — A different sequel can be presented as an exact source.** [audiovisual_scraper.py](C:/Users/user/animeapp/UniversalMediaOS.Core/audiovisual_scraper.py:257) accepts a candidate with only two matching title tokens and no candidate year. The C# provider stamps the requested identity onto the result, and [AudiovisualSourceResolver.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualSourceResolver.cs:96) adds scraper results without applying its strict matcher.

| Requested title, year | Candidate with no year | Actual score | Outcome |
|---|---|---:|---|
| Dune Part One, 2021 | Dune Part Two movie | 28 | Accepted |
| Spider Man No Way Home, 2021 | Spider Man Far From Home movie | 38 | Accepted |

These executed probes establish a matching defect, not that a particular live website served the wrong film. Preserve candidate identity evidence and stable provider IDs, validate title/year/episode consistency, and distinguish unresolved candidates from verified matches.

**MOV-5 · P1/P2 — Catalog items lose the provider identifier needed to resolve themselves.** The Archive identifier survives only in a poster URL, while the identity model retains title/year/TMDB fields. Source lookup searches again. A card may have no year, but [ExactAudiovisualMatcher.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ExactAudiovisualMatcher.cs:39) rejects missing-year matches without a common TMDB ID. The live feed contained such cards. Carry the Archive identifier through the model and resolve the known record directly.

**MOV-6 · P2 — Language and availability claims can be misleading.** [ScraperAudiovisualSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ScraperAudiovisualSourceProvider.cs:92) labels audio using the user's requested language, or English by default, rather than verified track evidence. Separately, provider failures collapse into empty arrays and the UI reports no matching titles. Represent unknown audio honestly and return an error/provider state independently of result counts.

The [detailed movie audit](C:/Users/user/animeapp/docs/qa/movies-audit-2026-09-09.md) covers the complete discovery-to-playback path, pagination, configured-key behavior, and relevant test gaps.

## Performance and reliability findings

**PERF-1 · P1 — Movie source discovery creates excessive work and withholds early successes.** Each details lookup can search ten sites, collect up to 24 candidates, and launch a separate resolver process for each candidate. Each can also launch Chromium and retry alternate providers. There is no C# concurrency limit at [ScraperAudiovisualSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ScraperAudiovisualSourceProvider.cs:61). All results are awaited before display, even if an Archive/configured source is already ready. The search deadline is 14 seconds and each subsequent resolver has a 45-second timeout; Archive metadata resolution can add sequential requests of its own. Bootstrap is outside these budgets.

Return sources incrementally, resolve the best candidates or the selected candidate first, limit browser/process concurrency, deduplicate provider work, and enforce an overall deadline. Async methods alone do not make this work cheap or make first results appear promptly.

**PERF-2 · P1 — Automatic anime playback can perform the same expensive search twice.** [TripleNetHandoff.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Routing/TripleNetHandoff.cs:107) calls the Python resolver in tier one. If it exhausts its search without a source, the automatic-provider branch calls that resolver again at line 149. With the default budget, two failed passes can consume roughly 146 seconds before later work; individual runs can be configured up to 120 seconds. Preserve and reuse the first result/fallback information and avoid repeating an exhausted crawl.

**PERF-3 · P1 — Dependency setup is outside request deadlines and can falsely report readiness.** [PythonBootstrapper.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Services/PythonBootstrapper.cs:192) installs five packages sequentially with a 60-second timeout per package. Scraper request timing starts only after setup. On failure, readiness is still based on Python/script file existence rather than successful dependency imports. A setup attempt can therefore take minutes and leave a broken scraper marked ready. Verify readiness explicitly, cache a successful dependency check, and include initialization within the caller's bounded operation or expose it as a separate visible setup task.

**PERF-4 · P2 — Dub filtering delays the whole search page.** [SearchViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs:462) checks availability and awaits all checks before displaying filtered results. Concurrency is bounded, which is useful, but slow checks still create a barrier across the page. Use cached evidence, show progress and incremental confirmed matches, and avoid blocking metadata display on optional enrichment.

**PERF-5 · P2 — Image downloads can outlive their views and header timeout.** [AsyncImageLoader.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/Controls/AsyncImageLoader.cs:62) cancels replaced URLs but lacks unload cancellation, shared in-flight request deduplication, and a global concurrency limit. It requests headers first; the client timeout does not cover the entire subsequent body read. Add an operation timeout spanning body transfer, unload cancellation, and bounded/deduplicated fetches. Expanded movie grids also need virtualization; the existing movie ItemsControl/WrapPanel materializes the cards.

**PERF-6 · P2 — One slow or timed-out book provider can hide all successful results.** [BookCatalogService.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Books/BookCatalogService.cs:48) waits for every catalog, including the Python path. HTTP timeout cancellation is not handled as a provider failure by its exception filters. The browse view catches cancellation without distinguishing a user cancellation from a provider timeout, potentially leaving the status at searching and discarding successful providers' results. Preserve partial successes, distinguish timeout from cancellation, and expose provider health.

The [detailed performance audit](C:/Users/user/animeapp/docs/qa/performance-audit-2026-09-09.md) records the supporting call paths, timing assumptions, and further lifecycle observations.

An additional live observation matters for diagnosis: the audit app's AniList requests returned HTTP 403 with a provider response stating that its API was temporarily disabled. That explains the empty anime page in this particular run. It does not explain the confirmed local UI or movie defects, and it is not evidence of a permanent outage. The response is in the isolated [audit log](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/ui-profile/UniversalMediaOS/app.log). No user account settings or secrets were inspected.

## Why the existing tests passed

The tests cover useful behavior: layout row regrouping, isolated service contracts, configuration handling, playback lifecycle/resume regressions, and selected desktop interactions. Their passing result should be preserved as a baseline.

However, important assertions do not cover the failures above. The fallback catalog test supplies one exact title and expects one result. The test named `IndependentCatalogServices_DoNotCrossMediaKindsWithoutTmdb` disables the fallback and checks empty arrays, so it never exercises mixed real metadata. Chrome tests check XAML declarations rather than resulting geometry or colors. Twenty-two scenarios are explicitly skipped, including several download, search, and settings cases. Those are coverage gaps, not passing validations.

The older reports' blanket claim that anime search virtualization is absent is no longer current: row virtualization/reflow is implemented and has regression coverage. Likewise, playback resume and cleanup have dedicated code and passing checks. The audit does not recommend undoing those repairs.

## Repair order and acceptance criteria

1. **Repair the shell and shared controls.** Separate navigation groups, stabilize Settings placement, fix dropdown sizing/template behavior, and style tab actions. Verify 900 and 1280 DIP windows, supported zoom/density settings, both themes, and RTL layout. No isolated Settings row or white native disabled buttons; compact selectors must fit their allocated toolbar space.
2. **Make movie identity and discovery trustworthy.** Establish a usable metadata provider path, genuine movie/TV classification, ranked partial searches, stable provider identifiers, and pagination. Test the default configuration and invalid/missing-key behavior. The two wrong-sequel fixtures must fail source verification, and movies must not be relabeled as television merely by switching tabs.
3. **Bound loading work and show partial progress.** Remove the duplicate anime crawl, cap resolver/browser concurrency, stop work when views close, and publish first useful results promptly. Use controlled slow/failing providers to measure first-result time, total deadline, cancellation, and process counts. Do not infer improvement from a warm-cache run alone.
4. **Close the test gaps and validate representative real flows.** Add targeted behavioral/geometry checks for these defects, retain existing regression coverage, and then check movie feature playback, TV episode selection, seek/resume, language reporting, and fallback failure messages. Follow with focused profiling if scrolling or playback still stutters after network/process fan-out is bounded.

These changes can be made incrementally. The audit identifies multiple specific defects; it does not support replacing the entire application or claiming that every subsystem is broken.
