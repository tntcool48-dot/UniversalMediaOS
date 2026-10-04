# Image request lifetimes and grid resources — October 4, 2026

Poster requests now share work by URL and decode width, stop when their final attached consumer leaves, and restart safely on reload. Actual second-monitor catalog paging and three Details tabs retained the correct posters. This closes the scoped image/grid implementation and measurement item; broader resource stability and player/release acceptance remain open.

## Problem and repair

The previous image loader canceled URL changes but did not cancel detached controls or restart canceled same-URL loads. Concurrent controls downloaded and decoded the same image independently, with no concurrency bound. Its HTTP timeout covered response headers but not a stalled streaming body.

The loader now uses four active fetch/decode slots, shared requests keyed by URL and decode width, and per-consumer cancellation. Canceling one consumer preserves other owners; final-owner cancellation removes the old request so a returning control can retry immediately. Loaded/Unloaded handlers own attachment state. URL/width changes clear stale images, and operation/key guards prevent late success or failure from replacing a newer poster. Successful frozen images survive detach/reload. Failure placeholders retry on reload, and only loader-owned error tooltips are cleared.

An 18-second active-request deadline now covers headers and body reads, including body streams that ignore cancellation. Existing content-type and declared/actual 25 MiB limits remain. The existing 150-entry poster cache remains; large manga pages are not cached. Native WIC decoding checks cancellation before and after decoding and retains its concurrency slot until it finishes; it cannot be forcibly interrupted mid-decode.

An added grid regression first failed because an unchanged final partial row was replaced during a result refresh. The row rebuild now retains that row when its items are unchanged. Existing recycling virtualization remains in place.

## Verification

Thirteen new cases cover stalled bodies, duplicate/final owners, queued cancellation, 1,000 consumers sharing 200 URLs with four active requests and a 150-entry cache, distinct decode widths, content/byte limits, detach/reload, stale results, failure retry and shared controls. Dedicated STA dispatcher tests exercise actual Image lifecycle handlers without visible windows. The existing partial-row regression passed after the repair.

The focused suite passed **46 checks**. The full C# suite passed **837 tests, zero failures, 22 existing skips, 859 total**, in **2 minutes 32 seconds**. Build completed with zero warnings/errors. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/image-lifetimes-full-2026-10-04.trx). Python source is unchanged; the latest full-suite result remains **178 passes**. Image commit `9202c34` passed [GitHub Release validation](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37218283304) in **4 minutes 27 seconds**, with 837 passes and 22 skips, build zero warnings/errors and Python syntax validation. This is distinct from physical/release acceptance.

The final Debug build ran with the existing isolated profile and secondary-monitor placement, at origin 2880,478:

- Paged the real AniList catalog from 36 to 72 to **108 items**, with visible posters after scrolling. A fresh accessibility snapshot exposed **12 card Details buttons** in the viewport/cache, consistent with virtualization; this is not a heap count of every WPF object.
- Opened independent Details tabs for **Naruto (AniList 20 / MAL 20)**, **Blue Box Season 2 (189123 / 61323)** and **You and I Are Polar Opposites Season 2 (210031 / 63832)**. Their correct posters survived switching and closing tabs, with all 108 catalog items retained on return.
- Closed all three Details tabs and the isolated app. The known QA process exited. No media player, download or book was opened during this image check.

Process samples are measurements of this one UI session, including automation and growing content, without forced collection or a paired previous-build benchmark:

| Stage | Working set bytes | Private bytes | Handles |
| --- | ---: | ---: | ---: |
| Warm start | 246,595,584 | 179,318,784 | 1,819 |
| 72 catalog items | 302,669,824 | 228,167,680 | 2,373 |
| 108 catalog items | 322,154,496 | 248,520,704 | 2,362 |
| Three Details tabs plus catalog | 343,867,392 | 272,310,272 | 2,350 |
| Details closed, catalog retained | 357,617,664 | 283,623,424 | 2,329 |

Private memory rose from about 171 to 270 MiB. These samples do not establish memory settling, absence of leaks or a performance improvement. Repeat-cycle and sustained-resource acceptance remains open. Grid scroll position reset to the top on tab return; scroll restoration is not established by this batch. The cache is entry-bounded rather than decoded-byte-bounded, and broader image formats remain unqualified.

Small screenshots, samples and preservation records remain in [ignored batch artifacts](C:/Users/user/animeapp/.artifacts/implementation/image-lifetimes-20261004). WPF DLL SHA-256: `02B6B391235DCAE50E3E181E3D8BE9986479757A8656A176BFCEE6EFEA58B53C`; Core: `3910209693F88248B908DD395889590EB731E43D26DD6443F29E68CFD7A712AA`. Normal build locations were reused; no cleanup was performed.

The isolated configuration is unchanged, SHA-256 `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`. Six permanent videos/captions/metadata remain intact, totaling **2,028,915,092 media bytes**. Separate positions remain Breaking Bad S1E1 **475.544s**, S1E2 **106.716s**, S2E1 **371.022s**, and Dune feature **318.291s**. No owned temporary jobs or unpublished copies remain. Book data and unrelated untracked work are preserved. Books remain deferred until other non-cartoon recovery work is complete; Arabic cartoons remain last.
