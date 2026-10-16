# Movie catalog record isolation, aliases and finite acceptance — October 16, 2026

One malformed Wikidata film could remove every valid neighbor and its continuation. Two recorded-Dune mutations (missing labels and invalid release precision) reproduced **two failures /zero passes**. Per-record data-shape errors now preserve neighboring identities and mark the page partial; non-object records are also isolated. Invalid batch envelopes retain the existing failed-page outcome. Cancellation, classification, IDs and continuation policy are unchanged.

The same batch repairs discarded catalog aliases. The existing five-entity request now requests aliases in its existing en/mul languages. Only observed same-entity, matching-language string entries are trimmed/deduplicated into alternate titles; missing/malformed optional alias groups preserve the film. No request-derived title, guessed original title, language inference, extra request or language crawl is introduced. The before scope had **two failures /two passes**: ordinary aliases and a valid mul group beside malformed en were lost. Explicit synthetic alias fields extend recorded fixtures; they are not presented as live captures.

The malformed-record/film/poster cohort passed **43 cases /zero failures/skips in 46 seconds**. Only the later alias/matching-risk scope then ran: **19 /zero failures/skips in four seconds**. These sets are disjoint: **62 unique passing cases**, with the original failed TRXs retained in `.artifacts/implementation/package-smoke-20261009/catalog-remakes-20261016`. Exact observed alias/title/year/form matching without a shared ID passes; wrong year, unrelated title and conflicting IDs still reject. Builds reused existing Release paths; no broad local or duplicate passing scope ran.

## Actual same-name Movie journey

Actual **d71ef78 /PID26076** launched with the existing private profile on the second monitor, **2880,478 /1280x800**. Normal Movies search displayed distinct 1922, 1979 and 2024 Nosferatu cards/years/posters. The 1922 and 2024 Details retained F.W. Murnau and Robert Eggers descriptions respectively. Independent Vidsrc lookups used **tt0013442** and **tt5040012**, returned verified Feature options and kept audio unknown. Two Archive candidates for 1922 remained unverified; none substituted for a verified source. No website fallback opened.

The original opened native changing frames and timed multiline English cues, then K-paused at **36.006 seconds**, duration displayed **1:28:40**. The remake's initial capture was black; a paused seek to **05:16** rendered a modern scene. Resume then advanced through different frames to **347.943 seconds**, duration **2:12:44**, before K pause. Returning between the two native tabs retained their own paused frames/clocks. Exact rows are **Q151895 /feature /36.006** and **Q116361132 /feature /347.943**. Normal app close released both players and drained progress. All thirteen previous rows/settings and the separate private Frieren205.461 row/settings remain unchanged. This finite sample does not prove spoken language, exact cut, continuous full-duration captions/watchability or DASH. It predates the newer parser/alias repair and is not relabeled as current-source UI proof. Receipt: `catalog-remakes-20261016/nosferatu-native-ui-d71ef78.json`.

## Original finite catalog criteria

The original [IP05 sample](../IMPLEMENTATION_PLAN.md#ip05--qualify-the-default-keyless-metadata-provider) calls for representative coverage, rather than exhaustive Wikidata taxonomy or universal availability. Playback/source requirements remain separate.

| Catalog requirement | Retained evidence and remaining work |
|---|---|
| Keyless Movie/TV routing independent of legacy keys and Archive flag | FilmCatalogRecoveryTests credential/flag combinations; [Movie routing QA](film-catalog-2026-09-13.md), [TVmaze routing QA](tvmaze-default-2026-09-13.md). |
| Ordinary films, remakes/sequels, animated features and series with stable namespaces/year/form | Dune and Office fixtures; [film subclass fixed sample](film-subclass-coverage-2026-10-05.md), Big Buck Bunny short-film QA, TVmaze Avatar classification and the actual Nosferatu pair above. |
| Useful partial/punctuation search and separate upstream limitations | Production `big` and `Spider-Man` samples in [film QA](film-catalog-2026-09-13.md); upstream ranking is not promised as popularity. Missing posters/metadata remain explicit. |
| Paging/refill, failure versus empty, retained rows, stale/cancel rejection and real TV units | Existing CatalogPagingTests, CatalogViewModelPagingTests, TvmazeQualificationTests and production routing/episode tests. Actual UI Load More through production DI is the remaining finite catalog observation. |
| Preserve observed alternate names without weakening identity | This batch's alias and independent evidence matching checks; original-title/audio unknowns remain unknown. |

A preserved no-year Archive ID is already retained by catalog paging, but direct source resolution still repeats title search. That is an open original **provider-resolution** requirement (IP07), not proof supplied by catalog ID retention. It remains explicitly tracked alongside live provider/DASH/audio/cut acceptance. The catalog group stays open pending current-source production Load More; no broader provider or release gate is closed here.
