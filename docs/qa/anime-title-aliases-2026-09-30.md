# Anime catalog-title aliases in native episode resolution

**September 30, 2026. Scope:** D / IP07/IP11. This is a scoped alias repair, not completion of independent provider IDs, unknown/special units or full anime acceptance. The [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) remains the single progress tracker.

## Cause and change

Anime catalog entries already carry an English title and a romaji title under the same media entry. Playback sent only `OfficialTitle` to the Python resolver. Site search, own-card scoring and primary episode-page checks consequently used only that spelling. On September 29 the romanized **Sousou no Frieren** query failed after 112.807 seconds; the English **Frieren: Beyond Journey's End** title found a native Dub stream. The title gap could also reject a source card or primary heading that legitimately used the other catalog spelling.

The anime details action now passes those two existing catalog titles through the router, service protocol and Python CLI for both Stream and explicitly selected website playback. Adjacent-episode resolution retains the same titles. When the catalog provides a compatible English title, lookup uses it as the first search query. Search-page relevance, each candidate's own card/URL, the lower-confidence hydrated-slug path and the primary episode heading can match either compatible title. This reuses the current automatic EverythingMoe/The Index provider pool and existing site/global budgets; it adds no requests to an already successful search and no key, static provider selector, dependency, database migration or profile setting.

Alias input is limited to the English and romaji fields, deduplicated and bounded to 140 characters each. One-word aliases and any alias explicitly conflicting with the requested season, part or film/special form are excluded. Source page season/part/episode observations and requested URL conflict checks still use the original requested title and episode. A romanized match is recorded as `catalog_alias=True`; it is evidence of a catalog-backed title match, not an independent provider ID. No alias is invented when the catalog lacks one.

The regression tests also exposed an existing heading gap: a different show heading such as **Attack on Titan Episode 1** could be classified unknown because the episode number suppressed the title conflict. Descriptive unmatched headings now conflict; generic **Episode 1** headings can still be evaluated against a matching page title. Wrong episode 10, season 2, movie/part variants and unrelated watch-card URLs remain rejected.

## Verification

Final production Debug build: **zero warnings/errors**. **495 selected C# tests and 106 Python tests passed, zero failures/skips**, adding two C# cases and eleven Python cases. The new cases cover both playback choices, requested episode/audio and alias transfer, English search pages containing only romanized cards, browser cards, source headings, incompatible/generic aliases, wrong title/season/episode and generic heading behavior. Existing native-first, media-byte, HLS variant, caption, Dub and cancellation regressions passed. A before/after syntax comparison confirms nine unit/media/audio helper functions and both fetchable/failed-Dub guard branches are unchanged. Eight desktop-input classes remain excluded.

Fresh checks used the actual production **ScraperEngine**, **PythonBootstrapper**, copied final-build scripts and Python CLI with a private data root. The same 202 automatically discovered index entries from the preceding checkpoint were copied into that private profile and refreshed as a cache snapshot. Captured service results then passed through **TripleNetHandoff** and the production HLS proxy. They were not newly rendered in VLC, and no OS keyboard/mouse/activation was used. The browser runs offscreen; no main-monitor interaction was needed.

| Requested title and catalog alias | Fresh service time | Identity and handoff |
| --- | ---: | --- |
| `Sousou no Frieren` with the known English title | 15.826 s | Consistent episode 1; `catalog_alias=True`; native Tier 1, one caption, selected Dub group and validated HLS variant |
| `Frieren: Beyond Journey's End` with the known romaji title | 17.400 s | Consistent episode 1; canonical-title match; native Tier 1, one caption, selected Dub group and validated HLS variant |

Both use the current automatic index and Anikoto source, selected by the resolver. The earlier 112.807-second failure is historical and affected by network/provider variation; these samples do not prove a general latency or accuracy rate. The native media reports no embedded audio-language tag, so spoken English was not independently reconfirmed in this batch. The existing unverified-language notice remains. The previous dated native player check supplied actual video/seek/caption evidence separately; this checkpoint verifies fresh source and router handoff, not a new rendered frame.

## Remaining and evidence

Independent provider IDs, catalog entries without a usable alternate title, unknown/special unit mapping and other provider title formats remain open. So do next/previous UI, returned-frame/focus/resource, outage/cancellation at scale, changed-provider/restart resume and the rest of A/B/C/D. The agreed temporary watch download, native Movie/TV, Anna's Archive, Arabic-cartoon and shared release work follows the anime gate.

[Final executable](C:/Users/user/animeapp/.artifacts/implementation/anime-aliases-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [selected C# TRX](C:/Users/user/animeapp/.artifacts/implementation/anime-aliases-20260930/results/anime-aliases-final.trx), [Python log](C:/Users/user/animeapp/.artifacts/implementation/anime-aliases-20260930/python-final.log), [live summaries](C:/Users/user/animeapp/.artifacts/implementation/anime-aliases-20260930/live-summary.json), [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/anime-aliases-checkpoint.json). Before snapshots, scoped diffs, private source results, redacted service logs, isolated profile and diagnostic driver/runtime are retained under the ignored evidence directory. Signed source/request data stays private.
