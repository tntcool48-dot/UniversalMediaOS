# Metadata qualification fixtures

The TVmaze JSON fixtures were captured September 10, 2026 from:

- https://api.tvmaze.com/search/shows?q=the%20office
- https://api.tvmaze.com/search/shows?q=avatar
- https://api.tvmaze.com/shows/526/episodes?specials=1 (203 entries: 202 regular episodes and one special; only identity, name, type and numbering fields retained)

Only fields used by the qualification adapter/tests are retained. These are recorded upstream data, not invented expected payloads. TVmaze API data is supplied under its documented CC BY-SA license; see https://www.tvmaze.com/api#licensing. Attribution: TVmaze. Fixture reduction is a modification. No API keys, cookies or session data are included.

The Wikidata fixtures were captured September 10, 2026 from its Action API (`https://www.wikidata.org/w/api.php`). The bounded typed probe in `tools/qualify-keyless-metadata.py` records search and batched entity requests. Search order, entity labels and relevant claims are retained; unrelated claims and reference blocks are omitted. Wikidata structured data is CC0: https://www.wikidata.org/wiki/Help:Data_access. The Toy Story fixture comes from the successful initial typed probe; a subsequent live retry returned 403, recorded separately in the evidence report.
