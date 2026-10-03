# Universal Media OS: Git and earlier task comparison

Completed September 9, 2026. This extends the [codebase audit](C:/Users/user/animeapp/docs/qa/codebase-audit-2026-09-09.md) with GitHub, local Git, and saved task evidence. No production files were changed, no branches were reset, and no historical patch was applied.

## What the history establishes

1. **The intended tabbed UI did separate the two sides.** The June redesign placed media destinations on the left, a flexible spacer in the middle, and Watch Together, My List, Downloads, and Settings on the right. This is a better restoration reference than GitHub's older sidebar.
2. **The current single navigation strip is traceable to the July 18 frontend cleanup.** That task describes replacing clipping with wrapping navigation, and its saved source output already contains today's single `WrapPanel`. The original child patch was not recovered, so attribution is to the recorded task, not an assertion about an individual model's exact edit.
3. **The historical three movies were real placeholders:** Spirited Away, Your Name, and Akira. They were hardcoded cards with movie playback explicitly unconnected. That code is absent from today's source; it is a plausible explanation for an older three-card experience, not proof of which executable the user saw.
4. **Today's movie problems are different and independently verified.** Keyless discovery uses an uncurated Archive page, Movies and TV can receive the same records, and source matching can accept the wrong sequel. The modern movie subsystem did not exist at the published Git baseline.
5. **The slowness accumulated across multiple rounds.** Python readiness was added to the request path during the June redesign. The July Sol-addressed audit added a second full resolver call during automatic fallback. Some image-loading weaknesses already exist in published `main`.
6. **Git alone cannot reconstruct the working pre-regression app.** The latest published commit is June 17; the tabbed shell and modern other-media subsystem were developed in uncommitted work afterward. Saved task patches supply missing evidence, but are not a complete, verified release snapshot.

## Git baselines and limits

The remote was verified with `git ls-remote --symref origin HEAD refs/heads/main refs/heads/master`. The supplied repository and local `origin` agree.

| Baseline | Verified state | Value as a reference |
|---|---|---|
| GitHub default `master` | `a2c51767be359fc135dcc1711d7a3d4500718778`, June 10 merge | Older monolithic shell; does not represent the requested tabbed design. |
| Published `main`, also local HEAD | `715629d29578e31ed90a5642895d8fcb61ca2dfb`, June 17 | Later MVVM shell with a 200-DIP sidebar divided into Menu, Active, and System groups. |
| Current working directory | Extensive tracked edits and untracked source files after HEAD | Contains the tab host, modern controls, Movies/TV/Cartoons/Books implementations, and later fixes under audit. |

The default branch is older than `main`. There are nine commits on the `main` side since the shared ancestor; the extra `master` commit is its merge. The local repository has complete history, no saved stash, and no post-June-17 commit snapshot. Remote inspection returned no tags. These facts explain why simply opening the repository's default page shows a substantially different app. [Default-branch commit](https://github.com/tntcool48-dot/UniversalMediaOS/commit/a2c51767be359fc135dcc1711d7a3d4500718778), [published main commit](https://github.com/tntcool48-dot/UniversalMediaOS/commit/715629d29578e31ed90a5642895d8fcb61ca2dfb).

The published shell's last committed change was `960ad61`, June 11. Current `MainWindow.xaml` differs from HEAD by 449 added and 63 removed lines. `UniversalMediaOS.Core/OtherMedia`, `audiovisual_scraper.py`, `book_scraper.py`, and the modern audiovisual views have no committed baseline. Git authorship cannot assign the accumulated worktree to Sol, Gemini, or any single task.

## Reconstructed sequence

| Period / task | Evidence | Consequence |
|---|---|---|
| June 10–17 Git commits | UI architecture refactors `f13402a` and `960ad61`, followed by playback/scraper fixes through `715629d` | Published sidebar-era baseline. |
| June 17–19, **Implement Figma design in code** | The user rejected the first sidebar interpretation, supplied screenshots/chat, and explicitly requested tabs plus placeholders for unprepared media. Saved add-file patches preserve the new shell and seeded cards. | The tabbed design was intentional. Left/right grouping existed from its first implementation. |
| Same June task | A saved patch adds global ComboBox `MinWidth=220`; another adds `EnsureScraperReadyAsync` before scraper execution. | Some current control and first-use waiting problems predate the Sol-addressed audit. |
| July 12–18, **Audit app and fix issues** | The opening request addresses Sol and already reports a slow scraper. Later patches repair playback, fallback, scaling, and window dragging. | This task did not start from a clean Git checkout, and the app was already slow. |
| July audit follow-up | The automatic WebView patch adds a second `_scraper.ResolveAsync` in Tier 2. | A failed automatic Tier 1 resolution can repeat the full search during fallback. |
| Later July audit messages | User explicitly asks to leave current Anime intact while adding separate media systems. The task later reports replacing non-anime placeholders. | A previous working movie catalog cannot be inferred from the placeholder UI. |
| July 18, **go through and inspect geminis work, tell me what you think** | The task finds an incomplete TMDB-to-IMDb migration, then reports restoring configured TMDB integration and hardening several services. | Metadata selection changed again. Its initial assumption that the entire worktree belonged to Gemini is not reliable attribution. |
| July 18, task `019f759d-c90f-7960-acfc-d801dc215c84` | User requests scraper integration, better button spacing, missing buttons, and merged provider settings. Recorded commentary says navigation now wraps; source output at 15:54 UTC shows the current navigation structure. | Strong session-level provenance for flattening the header and adding Player/tab actions. |
| August 8, **Review codebase for bloat** | Reports removing unused placeholder media files, legacy windows, dead methods, and unused theme resources; 242 passed / 22 skipped. | Historical placeholder files were removed. Deleted unused shadows/easings alone do not establish a visual regression. |
| **Compile latest clean build** | Reports compiling/package creation from the working files, with 242 passed / 22 skipped. | A successful recent build did not validate the catalog's relevance or the original layout. |

Task titles above are retained as returned by task retrieval. Where retrieval returned no title, the task ID is used. Earlier assistant claims of successful visual checks, speedups, or complete fixes are historical claims, not substitutes for current verification.

## UI: the useful restoration reference

The first saved tabbed shell uses this relationship:

```text
Brand + Anime / Manga / Movies / Books / TV / Cartoons
                         flexible space
                         Watch Together / My List / Downloads / Settings
Separate row: browser-style tabs
```

Its Grid has `Auto`, `*`, and `Auto` columns. The media StackPanel occupies the first column; the utility StackPanel occupies column 2. [Recovered original shell](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/initial-tabbed-MainWindow.xaml).

Today's shell gives the brand and window controls their own title bar, then puts all twelve destinations into one left-aligned wrapping panel. This explains the unused space to the right at normal width and Settings wrapping alone at minimum width. [Current shell](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml:156).

The July 18 task's stated goal was to avoid clipping and improve spacing. Wrapping solved one symptom while discarding the earlier separation of media and utilities. Its saved source output matches the current structure closely, including the separate title bar. The task also introduced tab movement controls; the current native button rendering explains the white disabled rectangles measured in this audit. The captured parent messages are preserved in the [provenance file](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/provenance.json).

The July window-drag repair is a different change. Its recovered patch adjusts window dimensions, chrome hit testing, resize behavior, and maximize/restore state. It does not introduce the single wrapping panel. Restoring grouping should retain those useful window fixes.

The global dropdown width came earlier: turn `019ed82d-5569-74d0-95bb-78590ff6ac50` in the June design task explicitly adds `MinWidth=220`. That remained global when later playback controls requested much smaller widths. [Recovered width patch](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/dropdown-minimum-width.patch). Current rendered measurements show a requested 72×34 selector becoming 220×38, with a roughly 62-DIP inner visual. Width policy and the nested ToggleButton template both need correction; reverting the whole design is unnecessary.

Recommended UI repair: preserve the current tab system and functional commands; restore separate media and utility groups with a flexible spacer at wide widths, and an intentional narrow-width arrangement. Keep Settings in the utility area. Define compact and standard control sizes and provide themed templates for tab actions. The original shell is a structural reference, not a file to copy over current code: it lacks later lifecycle, accessibility, localization, and window fixes.

## Movies: three placeholders versus the current catalog

The recovered `PlaceholderMediaViewModel` has exactly three entries in its Movies branch:

| Title | Year |
|---|---|
| Spirited Away | 2001 |
| Your Name | 2016 |
| Akira | 1988 |

The original note says movie streaming is not connected and the cards reserve the layout. A later June patch changes the wording to recommended seeded movies while the provider is prepared, without making that branch a real catalog. [Recovered placeholder source](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/initial-placeholder-media.cs).

No references to this placeholder class or these three seeded titles were found in current production C#/XAML. The August cleanup also explicitly records deleting the unused placeholder implementation. Therefore the historical three-card design is confirmed, while a current hardcoded three-movie limit is not. An older executable could explain the user's experience, but this audit did not establish that executable's identity.

The fresh current-build check with no TMDB key returned 50 Archive uploads, including test media and editing templates; TV Shows returned the same 50 titles. That is the current defect, independent of the old placeholders. The router chooses TMDB only when configured; otherwise it uses Archive. The Archive path retrieves one page, applies overly strict title filtering, and assigns the requested tab's media kind instead of establishing the record's actual type. [Current router](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogMetadataRouter.cs:38).

Source resolution has another independent fault: Python accepts weak title-token matches, the provider assigns the requested identity to the result, and the resolver bypasses strict identity matching for scraper results. Executed fixtures accepted Dune Part Two for Dune Part One and Far From Home for No Way Home. More scraper sites will not repair catalog discovery or this identity loss. [Movie audit and executed evidence](C:/Users/user/animeapp/docs/qa/movies-audit-2026-09-09.md).

Recommended movie repair: establish a useful, paginated metadata catalog; preserve stable IDs and actual movie/series type through discovery and resolution; validate candidate title/year/episode evidence; keep language unknown until verified; and display provider failure separately from an empty catalog. Rolling back to either published Git branch would not restore a working modern movie implementation.

## Performance: inherited weaknesses and later amplification

| Path | Historical evidence | Current implication |
|---|---|---|
| Automatic anime fallback | July saved patch adds another `ResolveAsync` in Tier 2 after Tier 1's resolver. [Patch](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/automatic-webview-retry.patch). | Failed automatic resolution can pay for two complete runs. The current default code budgets allow about 2×73 seconds; this is not a measured user-session duration. |
| Python setup | June patch adds awaited readiness before scraper execution. [Patch](C:/Users/user/animeapp/.artifacts/audit-2026-09-08/history/bootstrap-before-scraper.patch). | Fixes a startup race but makes first-use requests wait for setup outside the scraper timeout. Current five-package installation can encounter five sequential 60-second waits when dependencies are missing. |
| Python readiness result | The June ready-lock patch sets readiness from Python/script existence after installation. | Current readiness can still be marked true despite failed imports. The July combined import probe is a useful improvement but does not fully address this state issue. |
| Movie source resolution | Entire subsystem is after the Git baseline. Current code starts one resolution for each distinct search URL, then awaits all. | Up to 24 resolver processes, each with a 45-second limit and potential browser work, can contend and delay usable results. This is structurally different from old anime-only routing. |
| Image loading | Published `main` already uses headers-only HTTP completion and lacks unload cancellation. | These weaknesses predate the redesign. Current background image decoding is an improvement worth preserving; body deadlines, cancellation, and bounded concurrency remain incomplete. |
| Result publication | Current dub filtering and book aggregation await batches before publishing useful results. | Slow external work can keep the UI looking empty even while some results are ready. |

The Sol-addressed audit also removed expensive alternate search/extract work, fixed lifecycle issues, and introduced virtualization. Its reported speed improvement was for a particular smoke path. That does not contradict the duplicate failure path or establish a global speedup. The older claim that current anime results lack virtualization is stale.

Recommended performance repair: reuse the first resolution outcome, share a cancellation/deadline across the user action, initialize dependencies once with explicit readiness/failure status, bound browser/process concurrency, and publish usable results without waiting for the slowest candidate. Compare first-result and playback-start timings separately on successful, failing, and canceled flows. Detailed paths and limitations are in the [performance audit](C:/Users/user/animeapp/docs/qa/performance-audit-2026-09-09.md).

## Verification and preservation

The original current-code audit built successfully with zero warnings/errors and ran 264 tests: 242 passed, 22 skipped, zero failed. This historical follow-up did not change production code, so the application suite was not rerun. Exported evidence files are copies under `.artifacts`; their source task/turn IDs and truncation status are recorded in `provenance.json`. No truncated patch is presented as a complete source snapshot.

The next implementation should be a focused repair using the recovered tabbed layout as its reference, with separate changes for discovery/identity and loading behavior. Preserve a snapshot of all current tracked and untracked source first. A Git reset would discard accumulated fixes without recovering a verified complete version of the app the user remembers.
