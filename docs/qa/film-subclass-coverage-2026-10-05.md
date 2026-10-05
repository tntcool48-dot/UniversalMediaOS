# Wikidata film subclass coverage — October 5, 2026

The production Movie service reproduced the recorded **Your Name** miss: its first page contained five other films, with the intended 2016 film absent. Wikidata identifies that film as `Q21697406`, IMDb `tt5311514`, but its direct P31 claim is **anime film** (`Q20650540`). The catalog's query and parser only accepted film (`Q11424`) and animated film (`Q202866`). Spirited Away and Akira use the same excluded anime-film class; Frankenweenie uses animated feature film (`Q29168811`).

## Repair and identity boundaries

The typed query and entity parser now share the same accepted classes: film, feature film (`Q24869`), animated film, anime film and animated feature film. The animation-only query/parser uses the three animated classes. Targeted Action API captures confirmed anime film → animated film → film, animated feature film → animated film/feature film, and feature film → film through non-deprecated P279 claims. No title-specific override, runtime class crawl or new provider/key was added.

Both discovery and parsing need the extension: adding a query class alone would fetch the intended item and then discard it during enrichment. Non-deprecated P31 evidence remains required. Same-name novel/song metadata and deprecated subclass claims remain rejected, even with the intended film's IMDb ID. Exact entity IDs, distinct IMDb IDs, release-year precision, shared-language label fallback, optional exact-ID artwork and continuation/cancellation/error handling remain in place.

Animation is true only with an accepted animated class; other films retain an unknown animation state. No spoken language is inferred from animation, a country, a description or a subtitle. TVmaze TV and automatic anime provider discovery are unchanged. Updating the shared animation-only filter does not qualify Arabic-cartoon providers or audio.

The five-entity page, 4 MiB response limit, 12-second metadata timeout and bounded optional artwork budget remain unchanged. The existing qualification tool uses the same expanded typed filter and includes Your Name, Spirited Away and Akira in its optional typed-film sample.

## Regression and live service evidence

Recorded October 5 search/entity fixtures preserve Your Name's first page, a five-item feature-film page and a two-item animated-feature page. Fixture provenance and reduction are documented in the metadata fixture README. The new tests failed on five positive subclass cases before the repair; 16 other cases passed. After the repair, **62 scoped C# tests passed, zero failures/skips**, covering these cases and existing catalog, TVmaze, remake, paging, exact-ID artwork and failure boundaries. [Scoped results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/film-subclasses-catalog-2026-10-05.trx). The deliberately failing before-run is `film-subclasses-before-2026-10-05.trx`.

The production `MovieService`, production HTTP factory and isolated configuration then queried Wikidata without a TMDB key. These are single network-dependent samples, not latency guarantees:

| Query | First intended result / exact ID | Items / posters | Seconds |
| --- | --- | --- | --- |
| Your Name | Your Name (2016), Q21697406 / tt5311514 | 5 / 5 | 3.78 |
| Spirited Away | Spirited Away (2001), Q155653 / tt0245429 | 4 / 3 | 3.05 |
| Akira | Akira (1988), Q1905968 / tt0094625 | 5 / 5 | 4.39 |
| Toy Story | Toy Story (1995), Q171048 / tt0114709 | 5 / 5 | 5.42 |
| Dune | Dune (2021), Q60834962 / tt1160419; 1984 film also retained | 5 / 5 | 4.20 |
| Frankenweenie | Frankenweenie (2012), Q1051023 / tt1142977 | 1 / 1 | 1.79 |
| Froggie | Froggie (2024), Q137395955; IMDb unavailable | 2 / 1 | 1.98 |
| Browse page 1 | Five typed films | 5 / 4 | 5.81 |
| Browse page 2 | Five further films, no entity overlap with page 1 | 5 / 4 | 4.14 |

Before the repair, Your Name's first page took 4.22 seconds and contained Call Me by Your Name, Your Name Engraved Herein, Your Name Is Justine, Your Name Here and Your Name Poisons My Dreams. Afterward the intended film appears first. Partial metadata/artwork states remain visible for incomplete samples. Private captures and the reused service probe remain under `.artifacts/implementation/movie-coverage-20261005`; no signed playback URLs, captions or media copies are published.

## Visible catalog check and preservation

The rebuilt Release executable, PID **32072**, used a separate small QA profile. After verifying stable **2880,478 / 1280×800** placement on the second monitor, the actual Movies search showed Your Name (2016) first, its correct poster, **Language unknown**, five results and Load more. Clicking that card opened the 2016 Makoto Shinkai film's details with the same poster and unknown language. No source lookup, stream or download was started: this is catalog-to-details acceptance.

The first isolated launch used a config in the wrong QA directory and appeared on the main monitor. It was closed normally. After correcting the isolated config path, a maximized secondary-monitor launch moved to the main monitor when the desktop helper restored/activated it. That launch was closed normally as well. Setting only this QA profile to normal-window startup produced stable secondary-monitor placement, verified before the accepted search/detail actions. The maximized-window restore problem remains open; these earlier attempts are not second-monitor acceptance evidence. No original profile was edited.

The catalog QA app was closed normally, and the existing paused/muted Pilot CC check was brought back on the second monitor. The user's CC-dropdown report remains open. Preservation probes before and after UI work retained all six permanent media files (**2,028,915,092 bytes**), modification times, metadata/caption hashes, original config and separate original positions: Pilot 803.205 s, S1E2 132.484 s, S2E1 371.022 s and Dune 318.291 s. Book/import data was not touched. Existing build locations were reused.

Debug Core and scoped-test Core SHA-256 both equal `823935582D928A5F25240E804258C1F2AEF9A594B3756DD599C3B34A3BF784A4`. Tested Release Core: `B2C465B888448E16407A5465B41ED572B1E3AB249DC517B1C91FBD269B06B90F`; Release WPF: `DB462D5C3DE0C5C8DFE0D1F7C17946F8FB2B3BBDC19D7E005E6254D206C50A7F`. Core/test/Release app builds succeeded with zero warnings/errors; the changed Python qualification tool passed syntax compilation.

## Remaining limitations

This repairs demonstrated direct-subclass omissions, not exhaustive Wikidata classification or item accuracy. Relevance still includes other Akira Kurosawa films for Akira and similarly named works for other queries; browse incoming-link order is not popularity. Incomplete/conflicting upstream metadata, artwork availability, provider-ID reconciliation and broader catalog coverage remain open. Discovery and exact catalog identity do not establish watchability, spoken audio, cuts or full-duration captions. Broader provider, Windows DPI/restore, packaged-app and release acceptance remains required. The 17 grouped unfinished checklist tasks remain open; Books are deferred and Arabic cartoons remain last among the other work.
