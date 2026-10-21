# Anime search: provider outage, empty and data-less replies in the real app — October 21, 2026

Replaces two skipped Search placeholders from the [skipped-test inventory](skipped-test-inventory-2026-10-05.md). Branch **codex/app-recovery** after **c2138b6**. Each case launches its own AppFixture profile on the second monitor. The fixture's mock AniList endpoint answers by search term: normal results by default, HTTP 503 for one term, an empty page for another, and `{"data":null}` for a third.

## Cases

| Test | Journey | Result |
| --- | --- | --- |
| `T1_Search_05_Safe_Error_Display_On_Scraper_Offline` | Search "Frieren" shows its card under "AniList results for "Frieren"". A search the endpoint answers with 503 shows "Anime catalog unavailable. Previous results retained; retry search.": the same cards stay, and the "No anime found" state is not shown. Searching "Frieren" again recovers normally. | Pass |
| `T2_Search_04_Scraper_Returns_Empty_Or_Null` | An empty page shows "No anime found" with no cards. A reply without data is logged as an incomplete catalog page and shown as "Anime catalog unavailable. Check your connection and retry search.", not as "No anime found". | Pass |

No app defect was found: the search view-model already separates a provider fault from a real empty answer.

The first runs failed on the test, not the app:

- The card count used the first list in the window, which is a filter combo box's dropdown. It now counts the "View Details" cards in `ResultsList` (`search-outage-empty-first-20261021.trx`).
- Clicking "Anime" before each search opened a fresh tab with recommendations, so the data-less reply correctly said "previous results retained". The tab is now opened once per test (`search-null-diagnosis-20261021.trx`).

## Verification

The two cases pass **2 / 2** in 22 s (`search-outage-empty-after-20261021.trx`). The whole Search class passes **7**, with three placeholders still skipped, in 28 s (`search-class-after-20261021.trx`). Skipped methods in the suite drop from **17 to 15**: nine Downloads, three Search and three Settings.

## Limits

- The outage is an HTTP 503 from a local mock, not a real AniList or network failure; timeouts and DNS failures are not exercised here.
- Filter-by-type, paging and fuzzy-ordering placeholders remain skipped.
