# Anime search paging, filters, ordering and accessible names — October 21, 2026

Replaces the last three skipped Search placeholders from the [skipped-test inventory](skipped-test-inventory-2026-10-05.md), and names four unnamed controls. Branch **codex/app-recovery** after **780fefa**. Each new case launches its own AppFixture profile on the second monitor; the mock AniList endpoint answers by search term and records the `page`, `status` and `sort` it receives.

## Accessible names (defect)

A new audit, `Search_EveryInteractiveControlHasAName`, lists every visible, keyboard-focusable control in the anime search view whose UI Automation name is empty. Before the repair it found **4** (`search-accessibility-before-20261021.trx`):

- the search box (its "Search anime series..." placeholder is a separate text block);
- the Status, Audio and Sort combo boxes, which had only tooltips and an adjacent caption.

A screen reader announces such controls only as "edit" or "combo box". Each now has an `AutomationProperties.Name` equal to its visible label: "Search anime series", "Status", "Audio" and "Sort". The view also gains the automation ID `SearchView`, so the audit stays inside it.

## Cases

| Test | Journey | Result |
| --- | --- | --- |
| `T1_Search_03_Search_Result_Pagination` | Page 1 returns 36 cards and reports a next page. Scrolling the results list to its end requests page 2, whose five cards are appended ("Paging 37"–"Paging 41"). Scrolling again at the end requests nothing more: exactly pages 1 and 2 were requested, once each. | Pass, 8 s |
| `T1_Search_02_Search_Filter_By_Media_Type` | Anime search has no media-type filter (films and TV are separate areas), so this covers its Status filter. "FilterProbe" shows "Airing Show" and "Finished Show". Choosing Status "Finished" searches again with `status: FINISHED` and shows only "Finished Show"; "Any" shows both again. | Pass |
| `T1_Search_04_Fuzzy_Match_Score_Ordering` | The app does not score matches itself: a typed query requests AniList's relevance order (`SEARCH_MATCH`), and the cards keep the provider's order. Choosing Sort "Top Rated" requests `SCORE_DESC`, and the cards follow the new provider order. | Pass |

## Verification

- The audit fails before the naming and passes after it. The audit, filter and ordering cases pass **3 / 3** (`search-filters-and-names-first-20261021.trx`). The paging case passed on its first run (`search-paging-first-20261021.trx`).
- The whole Search class passes **11 / 11**, with no skips, in 1 min 38 s (`search-class-names-after-20261021.trx`).
- Skipped methods in the suite drop from **15 to 12**: nine Downloads and three Settings.

## Limits

- Names only: Narrator or NVDA speech and focus order were not checked. The new names are English, like the visible captions of this view.
- The method name `T1_Search_02_Search_Filter_By_Media_Type` is kept for traceability, although it now covers the Status filter.
- Ordering is the provider's. Whether AniList's relevance order is good for a given query is outside the app.
