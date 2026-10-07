# Live catalog image and tab resource cycles — October 7, 2026

The current live AniList catalog and repeated production Details tab returns revealed an unintended pagination trigger. The original identities survived, but returning the catalog without scrolling appended pages. This finite check does not establish general memory stability or provider playback/audio acceptance.

## Baseline and evidence

The private helper reused the ordinary Release app assemblies from published player repair b878a55 and an owned isolated profile. It loaded three live AniList pages (108 cards and 379 tags), then repeatedly opened the same three exact works, returned to the first Details poster, closed the other two tabs, returned to the retained catalog and closed the final Details tab. It canceled optional Dub lookup; no playback, downloads, Books or real account writes were exercised.

The first attempt stopped on a private harness layout assumption after one cycle; the second stopped on strict catalog equality after an automatic page append. Both are excluded from completed-cycle acceptance and retained under initial-layout-probe / initial-catalog-assertion. The diagnostic was corrected to wait for the real loaded view and preserve the initial ID prefix while recording actual collection changes. No app fix was made for those harness assumptions.

The instrumented 20-cycle baseline passed selected-work/poster, original-ID prefix, independent background close and image concurrency/cache/drain checks. Its catalog nevertheless grew from 108 cards to at least **684 / page 19**, without scroll input; collection stacks show production SearchViewModel.LoadMoreAnimeAsync appending pages during tab returns. The phase samples stored the last count before another in-flight page completed, so the 648 marker is not the final actual count. This is unintended extra work, not a fixed-catalog pass.

Fresh before/after held markers qualified second-monitor **1920×1040** screenshots at 19:53:13 and 19:53:35–37 UTC: returned Reincarnated as a Sword Season 2 poster with AniList 159042/MAL 53913, followed by a seven-column rendered Anime catalog. Screenshot capture requested no accessibility text.

Image peak was **4**, cache **23**, and sampled active/pending requests drained to zero. After natural 60-second settling, separate two full collections left 1/60 closed Details models and 4/80 unloaded Details views; all 20 unloaded Search views collected. A fresh exact-owned-process heap trace found only one closed Details view/model with automation-provider roots (ScrollProviderWrapper / ElementProxy through peers); the other unloaded views no longer appeared. The retained current Search model/view were expected. This observed sample is not an all-closed-object collection pass.

The app exited normally at **19:56:12 UTC**. The private ClrMD tool was reused with exact type arguments, detached promptly, and produced no dump. Private evidence is in .artifacts/implementation/image-resource-cycles-20261007; helpers must be excluded from release packaging. Original profiles and permanent media were not used.

## Current repair and verification

A focused real-app localhost regression is being added to detect extra page requests on repeated catalog returns and verify genuine bottom scrolling still loads more. The source trigger and repair results remain pending. Ordinary-process collectibility will be checked separately without desktop inspection. Broader search/image/download/reader resources and sustained/package acceptance remain open.


The valid real-app paging regression failed before repair: expected one AniList page request, actual two after the first unscrolled catalog return (**4 seconds**). The earlier initial-render fixture miss (omitted required coverImage) and transient top-level UIA title-query error are excluded from this reproduction. The final test uses the already acquired MainWindow. SearchView now rejects unloaded/hidden views and zero extent/viewport geometry before its existing prefetch decision. The unchanged bottom-scroll assertion passed after repair (**one check, zero failures/skips, 7 seconds**). The final related cohort and live repaired cycles are underway.

One build attempt ran while the completed baseline probe was still holding its resource sample and failed to copy its locked Core DLL. The probe was released normally; the subsequent ordinary Release builds succeeded with zero warnings/errors. No reset or new app output tree was used.


The final related Search paging/layout, viewmodel cancellation/filter changes, progressive Dub availability, image request lifetimes and stale-image assignment cohort passed **52 checks / zero failures / zero skips**, in **25 seconds**. [Cohort results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/search-image-lifecycle-paging-20261007.trx). Both ordinary Release builds reported zero warnings/errors. Tested WPF/Core SHA-256: **12FD7C53EED737F41F0756653F263A862026F05C1F93968E8601894B90FF33AE** / **7DA66D8A0A2BE383B46514C536E00E39F7163CE3A3E3CE843B78F32F0E4C4FBF**. Production changes are limited to the Search view paging guard; no Python source changed. Live repaired cycles are still running.


## Repaired live cycle observation

The repaired production build completed the 20 tab cycles with the original **108 IDs in exact order**, no automatic catalog append, peak image concurrency **4**, cache **23**, and sampled active/pending work zero. Fresh held before/after markers qualified returned exact Details at **20:00:32–33 UTC** and the actual **108 results / page 3** rendered Anime catalog at **20:00:56–58 UTC**, both **1920×1040 on the second monitor**. The underlying production selections, exact poster binding/decode, retained return and older background close assertions passed. Natural settling and separate collectibility/shutdown are still running.


The repaired observed run finished natural settling and normal shutdown. As in the baseline, collection left **1/60 models and 4/80 unloaded Details views**, while **20/20 unloaded Search views collected**. Its fresh exact-owned-process trace found one closed Details view/model retained only through **RefCountedHandle → automation ScrollProviderWrapper / ElementProxy → WPF peers**; the other views no longer appeared. The intentionally retained current Search view/model were present. Requesting no accessibility text did not eliminate these observer roots. This observed sample is not an all-object collectibility pass. The exact app and any profile-matched browser descendants exited; no browser was needed by this workload. A fresh process is now repeating the same 20 cycles without any desktop queries, using the same isolated profile and exact repaired assemblies; collection/exit results are pending.


## Repaired ordinary-process collection and shutdown

A fresh process repeated **20 actual live catalog/Details cycles without any desktop queries**. Exact three-work selection and decoded poster bindings, first Details return, independent older-tab closes, unchanged ordered **108 catalog IDs**, one surviving catalog tab, image peak **4**, cache **23** and drained active/pending work passed. After **60 seconds of natural settling**, the separate two full collections left **zero of 60 closed model references, zero of 80 recorded unloaded Details-view references, and zero of 20 unloaded Search-view references alive**. These are recorded references; unique view-instance counts were not measured. The current Search model/view intentionally stayed open. A fresh exact-owned-process heap inspection found only those two active Search objects and **zero AnimeDetailsView/AnimeDetailsViewModel objects**. This qualifies closed-object collection for this finite ordinary-process workload; it does not relabel the observed automation-provider retention as a pass.

| Natural phase | Private bytes | Handles | Threads | Accumulated CPU seconds |
| --- | ---: | ---: | ---: | ---: |
| catalog warm | 248,270,848 | 2,418 | 40 | 1.969 |
| settle 20 seconds | 307,671,040 | 2,404 | 36 | 19.719 |
| settle 60 seconds | 308,613,120 | 2,390 | 34 | 19.750 |

Natural private memory stayed near **293–294 MiB** over the final 40 seconds, above the initial catalog warm value. This is a finite settling sample, not return to baseline or a general performance guarantee. The separate full-collection diagnostic changed generations 0/1/2 **95/50/11 → 97/52/13**, managed bytes **52,566,968 → 21,836,200** and private bytes to **278,974,464**; those forced results are not ordinary settling.

Normal shutdown returned at **20:04:52 UTC**, and the exact app process was absent afterward. No browser was initialized for this flow. Both repaired runs reused one isolated profile and ordinary Release output; no app/media copies or dumps were created. Protected original six permanent media/configuration/metadata/caption snapshots matched again. The read-only original SQLite check retained all five tables/eight resume rows with content SHA-256 **e3fd7e97b1eeb4b4dff8d38f09b04a4a8ad19dafd2a835fd1449b006778328b1**.

The scoped paging repair and finite search/image workload are complete. Broader sustained search/image performance, accessibility-provider lifetime, download/reader resource cycles, live-provider/audio and packaged-app gates remain open. Books remain deferred; Arabic cartoons remain last among non-Books work.


Published normally to GitHub main as **92fd5b5a26904b216785d416c7be6bbe6c81f49a**, committing only the six owned source/test/tracker/QA files. The six unrelated original untracked paths remain untouched. [Exact full hosted run 37679525796](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37679525796) is confirmed **in progress** for this SHA; no full pass for the paging change is claimed yet. The latest completed full checkpoint remains b878a55 (1,032 passes / 22 existing skips / zero failures).


## Exact full hosted result

Published paging checkpoint **92fd5b5a26904b216785d416c7be6bbe6c81f49a** passed [run 37679525796](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37679525796): **1,033 passes / 22 existing skips / zero failures / 1,055 total**, in **9.4572 minutes** (about **9 minutes 27 seconds**). Build reported zero warnings/errors; Python scraper syntax passed. The new SearchReturnPaging real-app regression passed in the full context (**6 seconds**). No production source changed after this tested checkpoint; the subsequent resource reports update evidence only. Broader provider/audio, transfer/cache, Manga resources and packaged-app acceptance remain open.
