# Progressive book search — October 4, 2026

Book search now publishes each provider page as it finishes. A slow or unavailable catalog no longer holds up usable results, and cancellation retains available cards with a terminal status. This qualifies search progression; exact-edition download, reader navigation/restart and release acceptance remain open.

## Problem and repair

`BookCatalogService` previously awaited every provider before returning a single merged page. The browser cleared/repopulated its collection only afterward. HTTP failures and malformed responses often became successful empty pages, while provider-owned cancellation could leave the screen saying it was searching after loading stopped.

An additive `SearchUpdatesAsync` API now emits cumulative pages and individual provider outcomes; the existing `SearchAsync` collector uses that same path. Matching, duplicate merging, relevance scoring, configured-provider ordering and existing result/concurrency limits remain. Visible cards retain their order while richer duplicates replace their own card and new matches append; the bounded ranked result set still determines membership.

The browser guards every publication, error and final state by its query generation. Cancel works for initial browsing as well as explicit searches. A newer query detaches from earlier work immediately, even if a provider ignores cancellation. Available results survive cancellation/failure; genuinely successful empty searches clear old cards. Previous-query results are identified while a replacement search is pending. Import and recent-history operations participate in the same cancellation/generation ownership so an older search cannot overwrite an import.

Open Library and Google Books now report HTTP/JSON failures separately from successful empty pages. Their existing HTTP timeout is linked through response-body parsing, keeping the production 30-second budget rather than shortening correct lookups. Caller cancellation remains cancellation. The Anna subprocess retains its 25-second process budget and the Python search's 20-second route budget; timeout/error responses propagate as provider outcomes. Blocked/unrecognized pages are not certified as zero matches. Exact edition matching, rights labels and downloaded hash/file validation from the preceding batch remain in place.

## Verification

- Focused C# book regressions: **59 passed**, zero failures/skips. Eighteen new cases exercise fast/stalled providers, duplicate enrichment, collector equivalence, initial-browse cancellation, iterator ownership, deliberately ignored cancellation, late old-query results/errors, import preservation, successful empty versus failed searches, HTTP failures/malformed JSON and both public providers' body deadlines/caller cancellation. [Focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/book-progressive-focused-2026-10-04.trx).
- The immediate fixture's usable page appears within 500 ms while another provider is deliberately held. Cancellation detaches within 500 ms in those controlled fixtures. The 100-ms stalled-body fixtures complete within the deadline plus 500 ms and dispose their responses; these are controlled timing bounds, not live-network latency guarantees.
- Python full suite: **171 passed**, zero failures, including seven new outcome cases. [Python results](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/python-full.log).
- Full C# regressions: **791 passed**, zero failures, **22 existing skips**, 813 total, in 2 minutes 32 seconds. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/book-progressive-full-2026-10-04.trx).
- Build: zero warnings/errors, using the existing build directories. Source, WPF-output and test-output `book_scraper.py` SHA-256 agree: `948CACA6AE65FF45E7301E01B88C6C106FD392C21BA2F5D28A862BF72F57683B`. [Build hashes](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/build-hashes.json).

## Actual second-monitor check

The production WPF app used the preserved isolated QA profile, with `StartupMonitor=Secondary`; captures have desktop origin **2880,478**, size **1280×800**. No real profile was used. Default browsing settled with 38 public-catalog cards, retaining them while Google Books and Anna's Archive reported unavailable. A live Matilda search settled with 44 cards and Google Books unavailable.

A short read-only observation sequence of a repeated **Pride and Prejudice** search first observed **38 cards with one catalog still pending at 2.591 seconds**, then **45 cards with terminal partial-results status at 3.964 seconds**. Google Books remained explicitly unavailable. The visible cards stayed usable while the remaining provider finished. These are observation times for this sample, not exact first-publication times or a general performance claim.

[Pending-provider frame](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/live-progress-8.png), [terminal frame](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/live-progress-13.png), [observation records](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/live-progress-observations.json). These screenshots precede the final wording/import-ownership refinement; the progressive search implementation is the same. After full regressions, the final tested executable was launched again on the second monitor: default Books visibly published 38 cards while one provider remained pending, then retained them with Google Books/Anna's Archive unavailable. [Final-build frame](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/final-build-books.png), [final-build observations](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/final-build-observations.json). The isolated app was closed afterward.

Read-only preservation checks retained all six permanent TV videos and their captions/metadata: **2,028,915,092 media bytes**. Film and TV positions remain separate and unchanged: Dune feature **318.291 s**, Breaking Bad S1E1 **475.544 s**, S1E2 **106.716 s**, S2E1 **371.022 s**. No owned temporary job or unpublished copy remains. [Preservation evidence](C:/Users/user/animeapp/.artifacts/implementation/book-progressive-20261004/preservation.json). The cleanup audit already records the separately completed cleanup; this batch performs no disk cleanup or build-tree copying.

## Remaining limits

Search availability does not establish a readable matched edition. This batch did not download an Anna EPUB/PDF or qualify reader navigation/restart. Open Library still supplies work-level edition fields, and conservative matching can reject mixed/poor metadata. Details edition lookup still waits for its asset providers and can collapse provider failures into an empty asset list; qualifying honest edition availability is the next book step. Physical cancellation/provider-owned timeout and broad all-provider/release acceptance are not certified by the live page sample; their coverage here is controlled regression evidence.

The preceding book commit `228b0d8f` also completed [GitHub CI](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37196440411) successfully; it predates this batch.
