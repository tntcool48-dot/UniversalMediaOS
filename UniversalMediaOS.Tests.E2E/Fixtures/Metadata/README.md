# Metadata qualification fixtures

The TVmaze JSON fixtures were captured September 10, 2026 from:

- https://api.tvmaze.com/search/shows?q=the%20office
- https://api.tvmaze.com/search/shows?q=avatar
- https://api.tvmaze.com/shows/526/episodes?specials=1 (203 entries: 202 regular episodes and one special; only identity, name, type and numbering fields retained)

Only fields used by the qualification adapter/tests are retained. These are recorded upstream data, not invented expected payloads. TVmaze API data is supplied under its documented CC BY-SA license; see https://www.tvmaze.com/api#licensing. Attribution: TVmaze. Fixture reduction is a modification. No API keys, cookies or session data are included.

The Wikidata fixtures were captured September 10, 2026 from its Action API (`https://www.wikidata.org/w/api.php`). The bounded typed probe in `tools/qualify-keyless-metadata.py` records search and batched entity requests. Search order, entity labels and relevant claims are retained; unrelated claims and reference blocks are omitted. Wikidata structured data is CC0: https://www.wikidata.org/wiki/Help:Data_access. The Toy Story fixture comes from the successful initial typed probe; a subsequent live retry returned 403, recorded separately in the evidence report.

The `wikidata-your-name`, `wikidata-feature-films` and `wikidata-animated-features` search/entity pairs were captured October 5, 2026 from the same Action API. Your Name used a quoted relevance search with `haswbstatement:P31=Q11424|P31=Q24869|P31=Q202866|P31=Q20650540|P31=Q29168811`, limit 5, offset 0. The feature-film sample used `P31=Q24869`, limit 5, offset 2; animated features used `P31=Q29168811`, limit 2, offset 0. Both class samples used `incoming_links_desc`. Batched `wbgetentities` requested English/shared-language labels and claims. Only IDs, labels/descriptions, continuation offsets and P31/P577/P345/P3383 rank/value fields remain; references and unrelated claims are omitted. These are recorded catalog metadata under Wikidata's CC0 terms, not playback or spoken-language evidence. The synthetic same-name/deprecated-claim negative cases live in the test source, separate from these recordings.
