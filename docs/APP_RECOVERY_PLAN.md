# Universal Media OS — recovery and improvement plan

**Prepared:** September 9, 2026  
**Status:** Original requirements and problem inventory. Implementation is in progress; use the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) for current status. The findings below retain their original audit context.  
**Purpose:** Move the current app from an inconsistent, slow experience to a dependable media browser and player, while preserving the useful work already completed.

**Execution document:** Use the [full implementation plan](C:/Users/user/animeapp/docs/IMPLEMENTATION_PLAN.md) for code contracts, file-by-file work packages, dependency order, migration, tests, and release commands. This recovery document remains the requirements and problem inventory.

This is the current repair plan. It consolidates the September code audit, live observations, executed probes, Git comparison, and recovered task history. It supersedes the older `docs/qa/implementation_plan.md` where findings or priorities conflict.

## 1. What needs to change

The app builds, but several important user journeys remain broken. Its navigation lost the intended separation between media and utilities. Shared styles distort compact controls. Movie discovery can show miscellaneous uploads as films and television, while source matching can accept the wrong sequel. Several operations perform duplicate work or wait for every provider before displaying useful results.

The recovery should proceed through small, reviewable changes to these paths. A complete rewrite or a rollback to GitHub would discard later fixes without recovering a known working general-movie implementation.

The finished app should provide:

- A coherent tabbed interface with grouped navigation, readable controls, predictable settings, and functional window controls.
- Useful movie, TV, cartoon, anime, manga, and book browsing, with clear distinctions between metadata, source availability, and actual playback/readability.
- Correct title, edition, media type, season, episode, and language handling wherever those concepts apply.
- Responsive navigation, bounded background work, visible progress, and cancellation that stops the work it owns.
- Playback that demonstrably starts and continues, with working seek, resume, source switching, and episode selection.
- Honest unavailable/error states and reproducible builds whose identity can be checked from the running app.

## 2. Evidence, uncertainty, and history

| Finding | What is established | What must not be inferred |
|---|---|---|
| Build and existing tests | Audit build succeeded with zero warnings/errors; 242 tests passed and 22 were skipped. | This does not establish that the UI looks right or that a movie can play end to end. |
| Navigation | Current shell places all 12 destinations in one left-aligned wrapping strip. Original tabbed design separated media and utilities with a flexible spacer. | The old Git sidebar is not the intended restoration target. |
| Dropdowns | A requested 72×34-DIP control rendered at 220×38; its inner visual was about 62 DIP wide. | Adjusting margins alone will not repair the shared template. |
| Three movies | The historical placeholders were Spirited Away, Your Name, and Akira, with movie playback unconnected. | A three-title cap was not reproduced in current source/build, and the user's exact older executable was not identified. |
| Current movie feed | A fresh profile without a TMDB key returned 50 mixed Archive uploads; TV returned the same 50 titles. | A larger result count alone would not prove a useful catalog. |
| Wrong identity | Executed scoring probes accepted Dune Part Two for Dune Part One and Far From Home for No Way Home when candidate years were absent. | These fixtures do not prove that a particular live site served the wrong film. |
| Loading delays | Duplicate resolver calls, excessive process creation, delayed result publication, and missing body deadlines are established in code. | Timeout budgets are not measured loading times or a performance profile of the user's session. |
| External providers | An audit-time AniList response reported temporary API unavailability. | That historical response does not establish a current or permanent outage. |

GitHub's default `master` is older than published `main`; published `main` ends at `715629d` on June 17. The modern tabbed shell and other-media system were developed in later uncommitted work. Saved tasks identify the July 18 wrapping-navigation cleanup, a June global dropdown-width change, and a July duplicate fallback-resolution patch. Attribution to one model for the whole current worktree is unsupported.

## 3. Preserve the useful work

Before implementation, make a recoverable snapshot containing both tracked and untracked source, project configuration, tests, and build instructions. Record the current commit, working-tree changes, runtime versions, and source/build identity. Exclude credentials, private profiles, caches, and generated binaries from a source commit; retain required assets and lockfiles. A snapshot of HEAD alone is insufficient.

Retain these capabilities and existing regression coverage while making focused repairs:

- Browser-style tabs, independent playback instances, tab ownership, and close/reorder behavior.
- Window dragging, resize, maximize/restore, and fullscreen fixes.
- Anime search virtualization, row reflow, visible-row enrichment limits, and provider throttling.
- Playback resume, cleanup, native audio/speed controls, and source-header replay.
- Download queue persistence, configuration persistence/migration, and the merged Providers & Scrapers settings section.
- Background image decoding, image-size limits, bounded completed-image cache, and existing logging redaction.

Keep changes to Anime small and separately reviewable, consistent with the earlier request to preserve it while adding other media. Shared infrastructure repairs may affect it, but they need focused regression checks. Avoid copying the recovered historical shell over current code: it lacks later fixes.

## 4. Prioritized work list

**P1:** required to address reported failures or prevent misleading results/stalled operations. **P2:** usability, scalability, or reliability work needed for a dependable release. **Gate:** a prerequisite or release check. Priorities describe recovery order, not whether every feature is currently broken.

| ID | Priority | Work | Evidence class | Depends on |
|---|---|---|---|---|
| R01 | Gate | Preserve baseline and identify the executable being tested | Git/history and validation gap | — |
| R02 | P1 | Restore grouped tabbed navigation | Observed + historical source | R01 |
| R03 | P1 | Repair shared control sizing and visual states | Rendered measurements | R01 |
| R04 | P1 | Establish useful default movie/TV discovery | Live + source | R01 |
| R05 | P1 | Preserve identity/type; add partial search and pagination | Source + live | R04 contracts |
| R06 | P1 | Verify source, episode, and audio evidence | Executed probe + source | R05 identity model |
| R07 | P1 | Bound movie resolution and publish early sources | Source | R05–R06 contracts |
| R08 | P1 | Remove duplicate anime resolution | Source + recovered patch | R01 |
| R09 | P1 | Make dependency preparation bounded and truthful | Source | R01 |
| R10 | P1 | Cancel and time-limit stalled HLS body transfers | Source | R01 |
| R11 | P2 | Preserve partial results and distinguish failure states | Source | R04/R07 interfaces where shared |
| R12 | P2 | Bound poster loading and virtualize audiovisual grids | Source | R03, R05 paging |
| R13 | P2 | Remove synchronous database work from UI paths | Source; runtime impact unprofiled | R01 |
| R14 | P2 | Align settings and action labels with real behavior | Source + validation gap | R02–R11 |
| R15 | Gate | Prove playback and reading through representative flows | Validation gap | R04–R11 |
| R16 | P2 | Verify resource ownership and cross-feature regressions | Source + validation gap | Relevant repairs |
| R17 | Gate | Close coverage gaps and produce a verified package | Test/build evidence | All release requirements |

The items below were originally planned; current implementation and acceptance states live in the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md). A code change, passing compilation, or an earlier assistant statement is not sufficient to mark a requirement complete; attach its acceptance evidence.

## 5. Required changes and completion checks

### R01 — Establish a baseline that can be restored and reproduced

**Problem:** There is extensive uncommitted work and more than one build output location. The historical three-card experience cannot be tied to a specific executable from current evidence.

**Change:** Preserve the current source snapshot, identify the launch path used for testing, and record a build identifier based on the source snapshot. Add a small About/diagnostics entry showing version, build identifier, and executable location. Test with a separate profile and record relevant nonsecret configuration, including whether metadata credentials are configured and whether dependencies were already prepared.

**Complete when:** The baseline can be rebuilt from its recorded source; a tester can identify which build is running; the original user profile remains recoverable; and cold/warm test conditions are documented. Capture representative current UI screenshots and latency traces before the corresponding repairs.

### R02 — Restore intentional navigation grouping

**Problem:** All destinations, including Settings, share one `WrapPanel`. At minimum width, Settings can land alone on another row.

**Change:** Use the recovered June tabbed layout as the structural reference. Keep media destinations together, a flexible spacer at wide widths, and utilities/Settings together on the opposite side. Keep tabs in their own row. Define a narrow-width arrangement deliberately—for example, separate media and utility rows or a labeled utility overflow menu. Keep Settings in a predictable utility position and keep Player/VA Detect reachable without crowding the media group.

**Complete when:** The supported 900- and 1280-DIP widths, maximized view, Windows DPI changes, supported content zoom/density, and English/Arabic layouts have no overlapping controls, clipped essential actions, or isolated Settings row. Check tab switching, closing, scrolling/reordering, keyboard access, and moving the window between monitors. Record actual rendered screenshots; XAML declarations alone are insufficient.

**Primary files:** [MainWindow.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml), [MainWindow.xaml.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml.cs). [Recovered design reference](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/initial-tabbed-MainWindow.xaml).

### R03 — Repair controls as reusable components

**Problem:** Global ComboBox minimum dimensions override compact playback sizes. A nested native ToggleButton draws an undersized inner visual. Tab action buttons retain white native disabled styling, and some hover/light-theme color pairs are difficult to read.

**Change:** Remove the global width assumption and provide explicit compact/standard control sizing. Make the dropdown template fill its allocated bounds and correctly present its selected value, popup, arrow, and focus state. Use shared themed templates for tab icons and buttons, with coordinated foreground/background colors for normal, hover, pressed, focused, selected, and disabled states.

**Complete when:** Compact selectors fit their allocated playback toolbar space; standard settings selectors remain readable; the 170-DIP language container does not contain a 220-DIP child; dropdown inner chrome fills the intended control; and disabled tab arrows render coherently in both themes. Verify long labels, keyboard selection, screen-reader names, and supported density settings. Establish contrast checks as a design requirement, not merely a screenshot preference.

**Primary files:** [Controls.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Themes/Controls.xaml), [PlaybackView.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml), [AudiovisualCatalogView.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/AudiovisualCatalogView.xaml), [SettingsView.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SettingsView.xaml).

### R04 — Make default movie and TV discovery useful

**Problem:** Production routing uses TMDB when a key is present and Archive otherwise. The current keyless path supplies miscellaneous videos rather than a dependable film/series catalog. A playback-source toggle can also remove discovery.

**Change:** Separate metadata discovery configuration from playback-source configuration. Select and validate a useful non-TMDB, keyless discovery strategy against real production routing, as clarified by the user. Replace TMDB routing after qualification while preserving legacy IDs and saved data; existing TMDB code is transitional. Archive can remain a source or a properly identified collection; its generic upload feed cannot stand in for a general movie catalog. Do not simply activate the legacy IMDb client: its parsing and classification require evaluation too.

**Complete when:** A fresh profile with no key can browse and partially search identifiable films and series through the chosen supported default path. Cover blank, valid, and invalid credentials; Archive enabled/disabled; provider outage; and genuinely empty searches. Provider unavailability must be visible, and existing results must not disappear unnecessarily. Relevance, classification, and pagination determine success—not the number of cards.

**Unresolved implementation decision:** The keyless metadata provider/strategy still needs feasibility work and live verification. Create a small prototype before committing the catalog architecture. If none meets the requirement, report that limitation explicitly and revise the product decision; an error message alone does not satisfy useful keyless discovery.

**Primary files:** [AudiovisualCatalogMetadataRouter.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs), [AudiovisualCatalogServices.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogServices.cs), [TmdbMetadataClient.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/TmdbMetadataClient.cs), [App.xaml.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml.cs).

### R05 — Preserve identity and type throughout the catalog

**Problem:** Archive items lose their stable identifier; tab selection rewrites media type; exact title equality removes useful partial matches; only the first page is retrieved. Mixed TMDB movie/TV results can also collide if deduplicated only by numeric ID.

**Change:** Carry a provider namespace, stable item ID, actual feature/series type, title/alternate titles, known year, and episode identity where applicable. Preserve these through cards, details, source lookup, favorites, and resume. Treat animation/cartoon classification separately from whether an item is a feature or a series. Rank partial discovery matches; apply strict identity checks later. Implement pagination, stable ordering, cancellation, and namespace-aware deduplication. Fetch subsequent records/pages where post-filtering would otherwise hide available valid items.

**Complete when:** A shared mixed fixture yields correctly classified Movie and TV results without relabeling every record. A cartoon film and cartoon series remain distinguishable. A known Archive item with no year resolves by its preserved ID. Movie and TV records sharing a numeric ID remain distinct. Partial queries, punctuation, alternate titles, remakes, and valid items beyond page one are covered. Switching query/filter cancels obsolete page loads without mixing results.

**Primary files:** [AudiovisualModels.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualModels.cs), [InternetArchiveSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/InternetArchiveSourceProvider.cs), metadata clients/router, and [AudiovisualCatalogViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs).

### R06 — Make a verified source mean the correct media

**Problem:** Python accepts weak token matches; the C# adapter stamps the requested identity onto candidates; scraper sources bypass the strict matcher. Requested language can be reported as actual audio without evidence.

**Change:** Extend the Python/C# contract to preserve candidate evidence instead of substituting the request. Verify stable ID or sufficient title/year/type/episode evidence before labeling a source exact. Keep unresolved candidates distinct from verified sources. Reject conflicting sequels, remakes, seasons, or episodes. A missing year is not automatic failure when a trustworthy shared provider ID establishes identity. Treat audio language, subtitle language, original language, and dub availability as separate facts; unknown stays unknown.

**Complete when:** Both existing wrong-sequel probes fail verification. Positive exact-ID and correct-title fixtures continue to pass. Tests cover same-title remakes, adjacent episodes, a later season, unavailable episodes, and missing metadata. Changing source or language preserves the chosen episode. Arabic/English fixtures distinguish verified, mismatching, and unknown audio. No source inherits a requested identity or language as proof of its own content.

**Primary files:** [audiovisual_scraper.py](C:/Users/user/animeapp/UniversalMediaOS.Core/audiovisual_scraper.py), [ScraperAudiovisualSourceProvider.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ScraperAudiovisualSourceProvider.cs), [AudiovisualSourceResolver.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualSourceResolver.cs), [ExactAudiovisualMatcher.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ExactAudiovisualMatcher.cs).

### R07 — Limit movie resolution work and expose useful results early

**Problem:** Details lookup can launch up to 24 separate resolvers, each potentially launching Chromium and repeating alternate-provider work. `Task.WhenAll` keeps early successful sources hidden.

**Change:** Introduce an operation-wide work budget and shared concurrency limit for resolver/browser work, not just a per-tab limit. Deduplicate attempts by candidate/provider/identity. Prefer known IDs, previously successful routes, and selected/promising candidates. Avoid resolving every candidate merely to display details; allow explicit playback/search actions to initiate expensive work. Publish verified sources incrementally, and stop or deprioritize alternatives once the user's requested playback succeeds. Cache with bounded lifetime and invalidate failed or expired sources appropriately.

**Complete when:** A fast valid provider becomes usable while a slow provider is still running. Instrumentation proves the configured process/browser cap across multiple tabs. Closing details or changing the selected item cancels owned queued and active work; a late result cannot overwrite a newer selection. Shared in-flight work remains alive for other active consumers. One total deadline bounds the operation, with preparation explicitly included or shown as a separate preparation stage.

### R08 — Resolve an anime playback request once

**Problem:** Automatic fallback repeats the full Tier 1 resolver with the same inputs. The default process budgets can total roughly 146 seconds across two exhausted passes, excluding setup and cleanup; this is a code calculation, not a measured typical wait.

**Change:** Carry direct-media and browser-fallback information through one resolution operation. Reuse that result instead of restarting an exhausted crawl. A second pass must have a distinct, justified strategy. Respect an explicitly chosen provider and reuse a known working series/audio route where appropriate, while retaining fallback behavior when it fails.

**Complete when:** A controlled failed Automatic request invokes the expensive resolver once, returns a clear outcome, and remains cancelable. Direct-media, browser-only, explicit-provider, Sub/Dub, and next-episode paths still behave correctly. Trace both success and failure paths; a warm successful smoke test alone does not cover this defect.

**Primary files:** [TripleNetHandoff.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Routing/TripleNetHandoff.cs), [ScraperEngine.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Services/ScraperEngine.cs).

### R09 — Make preparation and service health accurate

**Problem:** Missing Python packages can trigger sequential installation waits before request timing begins. Failed installation can still lead to a Ready state. Pip output is not drained correctly, and Settings uses weaker readiness evidence than real execution needs.

**Change:** Use one shared preparation state with preparing, ready, failed, and canceled outcomes. Drain stdout/stderr concurrently, bound total setup, verify required imports after installation, and cache only successful readiness. Propagate the same state to Settings and the status bar. Normal requests should reuse an already prepared environment. Evaluate distributing or maintaining a pinned runtime separately, rather than repeatedly installing during ordinary use.

**Complete when:** Missing dependency, failed pip exit, blocked output, setup timeout, cancellation, and simultaneous first-use requests produce correct states. Failed imports never produce Ready. Cancel stops owned setup work. The app remains usable while preparation is in progress and offers a meaningful retry/repair action. Warm requests perform no unnecessary installation.

**Primary files:** [PythonBootstrapper.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Services/PythonBootstrapper.cs), scraper engines, and [SettingsViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SettingsViewModel.cs).

### R10 — Stop stalled streaming transfers cleanly

**Problem:** HLS manifests, keys, and segments use headers-first HTTP completion, but their subsequent body reads have no effective application deadline or playback-session cancellation. Closing the listener does not terminate all active outbound work.

**Change:** Link each request to its playback session and proxy lifetime. Use complete-operation deadlines for manifests/keys and an appropriate inactivity timeout for streaming bodies. Track active requests and bound concurrency. Cancel and dispose owned requests on source change, tab close, or shutdown. Preserve valid cookie/header replay and existing redirect protections.

**Complete when:** A controlled server sends headers and then stalls before or during a body: the request reaches the intended timeout, releases resources, and produces a useful failure. Closing playback interrupts active transfers. A long stream that continuously transfers data is not killed by a short fixed total timeout. Switching sources cannot leave the old session serving or consuming data indefinitely.

**Primary file:** [HlsLoopbackProxy.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Streaming/HlsLoopbackProxy.cs).

### R11 — Show partial progress and meaningful failure states

**Problem:** Dub-filtered search waits for all availability checks. Book search waits for every provider, and one provider timeout can discard other successes. Movie provider errors can appear as “No matching titles.”

**Change:** Publish results as independent providers/checks complete. Keep existing useful results during transient failures. In a strict Dub filter, show confirmed matches progressively and keep unknown evidence clearly separate; do not silently claim unchecked titles are dubbed. Distinguish user cancellation, timeout, unavailable provider, zero matches, unsupported source, identity mismatch, and playback failure. Keep rate limiting and backoff protections.

**Complete when:** One fast provider plus one slow/failed provider produces usable partial results before the slow one finishes. Timeout is visible as a provider failure, not user cancellation. No operation leaves a permanent Searching state. A newer query cannot be overwritten by an earlier one. An invalid metadata key has a distinct message and the documented fallback policy.

**Primary files:** [SearchViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs), [BookCatalogService.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Books/BookCatalogService.cs), [BookBrowseViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/BookBrowseViewModel.cs), audiovisual request coordination/view model.

### R12 — Make larger catalogs affordable to render

**Problem:** Poster requests can outlive their views, duplicate the same URL, or stall after headers. The audiovisual card grid materializes its items, so broader discovery would increase rendering work.

**Change:** Add image body deadlines, unload cancellation, reload handling, shared in-flight requests, and bounded download/decode concurrency. Make cancellation subscriber-aware when requests are shared. Virtualize/recycle audiovisual cards as paging expands, preserving position and selection. Retain the existing bounded cache, image-size cap, downsampling, and background decoding.

**Complete when:** Repeated scrolling and tab opening/closing do not produce growing detached-view requests or browser/image work. Repeated simultaneous images share downloads. Unloading one consumer does not break another. A still-bound reloaded image can fetch again. A large deterministic catalog realizes only the needed visible/overscan containers and remains responsive; memory/work returns to a stable range after repeated cycles.

**Primary files:** [AsyncImageLoader.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/Controls/AsyncImageLoader.cs), [AudiovisualCatalogView.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/AudiovisualCatalogView.xaml).

### R13 — Keep persistence off interactive UI paths

**Problem:** Resume and related SQLite work still execute synchronously through UI entry paths. Actual impact on this machine needs measurement.

**Change:** Trace playback opening/closing, resume updates, and VA lookup with a realistically sized database and controlled lock contention. Move blocking persistence through a bounded, serialized service appropriate to connection ownership. Marshal only final UI updates back to the dispatcher. Preserve transaction ordering, migration, and final resume writes; avoid wrapping arbitrary shared-connection access in concurrent background tasks.

**Complete when:** Controlled slow/locked database work does not freeze navigation. Rapid playback changes save to the correct identity, writes remain ordered, and closing/reopening restores the right position. Failures are reported without losing the last good data. Profile evidence determines whether further persistence optimization is needed.

**Primary files:** [DatabaseContext.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Data/DatabaseContext.cs), [PlaybackViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs), affected service/view-model callers.

### R14 — Make settings and buttons describe what they actually do

**Problem:** Some settings descriptions conflate discovery and playback sources, service health can claim readiness incorrectly, and existing declaration-based button tests do not prove outcomes. The settings page itself still has separate navigation/content columns.

**Change:** Keep the merged provider settings page. Separate metadata configuration, source configuration, and preparation status within it. Review each visible action against its actual command and outcome; include Save, Refresh, Cancel, provider selection, playback load actions, tab controls, and language filters. Use explicit disabled states with useful explanations when prerequisites are missing. Keep diagnostic implementation details in diagnostics rather than ordinary media browsing.

**Complete when:** A compact action inventory maps each important button to a tested outcome. Saved values survive relaunch and affect the documented behavior. Changing playback-source availability does not silently erase metadata discovery. Ready, Failed, and Preparing agree with actual dependency checks. Keyboard-only navigation can reach and operate essential controls.

### R15 — Demonstrate complete playback and reading journeys

**Problem:** The audit did not establish a complete live movie playback session. Returning a URL or opening a web page is insufficient evidence of a working player.

**Change:** Validate discovery → details → correct source/unit → playback/read → seek/navigation → close → resume. Distinguish native playback from opening a browser fallback. Verify actual frame/position progress and audio, not just successful command dispatch. Record unavailable providers and failed samples alongside successful ones.

**Complete when:** The matrix below has evidence from the built app. Where an external source is unavailable, record the failure and distinguish a successful controlled integration test from a missing live validation. Do not declare the overall movie repair complete solely on controlled fixtures.

| Journey | Required checks |
|---|---|
| Movie feature | Correct title/version; advancing playback; audible audio; seek; close/reopen resume. |
| TV series | Explicit later-season episode and adjacent episode; correct content; episode-specific resume; unavailable episode behavior. |
| Cartoon feature and series | Correct animation and feature/series classification; episode handling where relevant; verified language/unknown language behavior. |
| Anime regression | Search, detail, Sub/Dub, native or supported browser fallback, next episode, and resume retain their intended behavior. |
| Standalone player | Local file and supported URL opening; readable controls; play/pause/seek; source failure and cleanup. |
| Books | Fast metadata survives a slow provider; supported book opens in the reader; navigation/progress works; unavailable download is explained. |
| Manga | Search, chapter selection, reading/navigation, tab lifecycle, and keyboard access. |

### R16 — Verify resource ownership and surrounding features

**Problem:** Repeated navigation and source switching can expose lifetime defects that isolated success tests miss. Other features have partial coverage and must not regress while shared services change.

**Change:** Exercise repeated tab cycles, active playback switches, cancellation, downloads, My List, and Watch Together with explicit ownership of sessions and background work. Verify that pending work cannot update a disposed or different tab. Review awaited shutdown and cancellation disposal where traces show surviving work. These are validation requirements and targeted improvements, not a claim that every listed feature is currently broken.

**Complete when:** Closing one tab leaves other active tabs functional; owned child processes and requests settle after cancellation; playback ownership and resume remain correct. Downloads preserve queue state across restart and honor pause/resume/cancel. My List preserves distinct media identities. Watch Together works across two controlled clients for its supported scope, without duplicate state handlers after reconnect. Shutdown does not corrupt pending persistence or leave app-owned background activity.

### R17 — Make completion and packaging evidence-based

**Problem:** Existing tests passed despite visible and semantic failures. Some tests examine declarations or empty fallbacks rather than the actual problematic behavior; 22 scenarios are explicitly skipped.

**Change:** Retain useful existing tests and add targeted behavioral checks for the defects above. Replace misleading coverage with tests through production dependency injection, rendered WPF geometry, actual command effects, mixed metadata fixtures, and controlled failure/latency servers. Review each skipped scenario: implement important release coverage or document precisely what remains unvalidated. Produce one clearly identified package from the recorded source and test that package.

**Complete when:** Builds and relevant suites pass; required skipped functionality has real coverage; visual and live-flow evidence is attached; the packaged executable identifies the intended source; and a clean-profile launch verifies startup, dependencies, settings, and the representative journeys. Every remaining limitation is explicit. Test counts alone do not close R02–R16.

## 6. Architecture boundaries to enforce

These boundaries can be implemented within the existing projects; they do not require a new framework or a wholesale rewrite.

| Responsibility | Input/output contract | What must remain separate |
|---|---|---|
| Metadata discovery | Query/filter/page → identified catalog items + paging + provider status | A title's existence versus a playable source being available. |
| Source discovery | Selected identity/unit/preferences → candidates carrying their own evidence | The requested identity versus the candidate's verified identity. |
| Verification | Candidate evidence → verified match, mismatch, or unresolved candidate | Ranking confidence versus proof of title/episode/language. |
| Playback/read session | Selected verified source → progress, failure, completion, resume | View navigation versus ownership of native/browser/proxy resources. |
| Operation coordinator | User action → deadline, cancellation, concurrency budget, partial results | Per-provider failures versus cancellation of the whole action. |
| Presentation | Results and explicit states → cards, controls, progress, actions | User-facing outcomes versus low-level logs and setup diagnostics. |

## 7. Implementation sequence

| Stage | Deliverables | Exit condition |
|---|---|---|
| A — Baseline | R01, representative failing fixtures, action/build inventory | Reproducible current snapshot and recorded evidence. |
| B — Usability and trustworthy catalog | R02–R06; UI work and metadata prototype can proceed independently | Grouped readable UI; useful chosen discovery path; stable IDs; wrong-title fixtures rejected. |
| C — Bounded operations | R07–R13, with R08–R10 able to start after baseline | No duplicate exhausted crawl; accurate preparation; bounded process/body work; partial results survive slow providers. |
| D — End-to-end behavior | R14–R16, completed as each feature becomes testable | Representative playback/reading and cross-feature journeys have recorded outcomes. |
| E — Release | R17, final matrix and package verification | All release gates below are met, with remaining external limitations stated. |

Each change should state the problem, implementation, preserved behavior, and acceptance evidence. Shared identity/result contracts should be agreed before parallel edits to dependent services. Avoid mixing styling, provider migration, and playback lifecycle changes into one difficult-to-review patch.

## 8. Performance measurements and targets

Do not use configured timeouts as proof of improvement. Record the executable/source identity, profile, machine, cold/warm state, provider configuration, and sample size for each comparison. Measure repeated successful and failing samples; report median/tail behavior and failures instead of one favorable run. Set numerical release thresholds from the baseline and controlled tests before declaring a performance repair finished.

| Measure | Required direction / hard invariant |
|---|---|
| UI feedback and responsiveness | Loading/cancel state appears promptly; delayed network/setup/database fixtures do not block unrelated navigation. |
| Time to first useful catalog result | A completed fast provider becomes visible before a deliberately slow provider finishes. |
| Time to first verified source | A verified ready source is selectable while optional alternatives remain pending. |
| Time to first frame/audio | Measure separately from resolving a URL and from opening a browser page. |
| Total user-action duration | An explicit encompassing deadline ends exhausted work with a visible outcome. |
| Work count | One exhausted automatic anime crawl; resolver/browser activity never exceeds its configured shared cap. |
| Cancellation/shutdown latency | Measure owned tasks/processes/requests returning to baseline; cancellation must not kill unrelated tabs or a user's browser. |
| Scroll/resource behavior | Large fixtures and repeated open/close cycles reach stable resource usage without accumulating detached work. |

Provider availability and network conditions remain external variables. A useful target is fast, correct success when a source is available and bounded, understandable failure when it is not; the plan does not promise that every title or provider will always work.

Use repeatable failure scenarios alongside the live measurements:

1. A local server returns HTTP headers and then stalls the manifest, key, image, or segment body. Check the configured body/inactivity deadline and cancellation cleanup; compare with a healthy continuously transferring stream.
2. One provider returns immediately while another stalls or times out. Confirm that the first results are visible and remain available after the second provider fails.
3. A counted fake resolver exhausts Automatic discovery. Confirm one full crawl, reuse of a returned browser fallback, and no new fallback work after cancellation.
4. Missing imports and failed or verbose pip processes exercise preparation failure and retry. Confirm that health never becomes Ready until the dependency checks succeed.
5. Repeat 20 open/search/play/cancel/close cycles and record outstanding requests, owned processes, and memory after each cycle. Exclude activity belonging to other live tabs when checking for leftovers.
6. Hold a database write lock for three seconds while exercising playback navigation. Record dispatcher responsiveness and correct final resume persistence. Set the allowed heartbeat gap from the test machine's baseline before running the comparison.

These are proposed test recipes, not completed tests. Attach their measured outcomes and the actual configured budgets to the repair record.

## 9. Release gates

- [ ] Current source is recoverable, and the package/build under test is unambiguous.
- [ ] Grouped tab navigation and shared controls pass the width, DPI, theme, zoom/density, and RTL checks.
- [ ] Fresh-profile discovery follows the supported default strategy and produces relevant, correctly classified, paginated records.
- [ ] Wrong sequels, remakes, episodes, and unsupported language claims cannot be presented as verified matches.
- [ ] Stable provider and episode identity survives details, playback, favorites, and resume.
- [ ] Fast successful providers remain usable when other providers fail or stall.
- [ ] Setup, resolution, body transfers, and cancellation honor their stated states and work budgets.
- [ ] Required representative movie/TV playback is demonstrated from the built app, with timing and failure records.
- [ ] Anime, manga, books, downloads, settings, and tab ownership pass the relevant regression matrix.
- [ ] The existing useful suite and new targeted checks pass; skipped coverage and remaining limitations are disclosed.
- [ ] A clean-profile run of the actual deliverable package passes; evidence is attached to the release record.

## 10. Evidence and handoff

This document is the execution checklist. The supporting reports retain detailed source references, calculations, screenshots, probes, and historical limits:

- [Full codebase audit](C:/Users/user/animeapp/docs/qa/codebase-audit-2026-09-09.md).
- [Git and earlier task comparison](C:/Users/user/animeapp/docs/qa/git-and-task-history-comparison-2026-09-09.md).
- [UI audit](C:/Users/user/animeapp/docs/qa/ui-audit-2026-09-09.md).
- [Movie/TV/cartoon audit](C:/Users/user/animeapp/docs/qa/movies-audit-2026-09-09.md).
- [Performance audit](C:/Users/user/animeapp/docs/qa/performance-audit-2026-09-09.md).
- [Recovered source and patch provenance](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/provenance.json).

For implementation, work through R01–R17 in dependency order and record the acceptance evidence against each item. Keep confirmed defects, proposed improvements, and unverified external behavior distinct. Preserve current fixes, use the recovered tabbed design selectively, and do not substitute demo cards, larger result counts, nonempty URLs, or successful compilation for a working user journey.
