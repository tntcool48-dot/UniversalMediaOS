# Bounded anime synonym fallback

**September 30, 2026. Scope:** D / IP07/IP11. The [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) remains the progress tracker; this is one matching repair, not the anime acceptance gate.

## Cause and change

The AniList catalog already retains synonyms for each entry, but the watch action passed only English and romaji titles. A provider using another catalog spelling could be missed entirely. If both those title fields were absent, the catalog displayed `Unknown Title` even when a native title existed. In a September 30 direct AniList sample, 18 of 50 less-popular anime lacked an English title; `Hen Zemi` had catalog synonyms. That one page is an example, not an overall missing-title rate.

The normal English/romaji title remains first. When it has no distinct trusted alternate and finds no candidate, one bounded AniList synonym can be tried on the same automatically discovered site before the existing deadline. The candidate must be ASCII-searchable, contain at least two meaningful terms, be no longer than 140 characters, and have no observed season/part/form conflict. At most the first eight synonyms are considered; one is chosen, favoring words shared with the requested title. Successful normal-title lookup adds no request. A native-only catalog title is shown instead of `Unknown Title`; if it and its synonyms cannot be searched safely, resolution returns `no_searchable_title` before visiting sites.

Synonyms need stronger proof than English/romaji titles. AniList's synonym list can refer to a related work: the sample included `Your Name` under a separate Suntory promotional entry. The fallback can discover such a card, but a page matching only a synonym must expose the selected entry's AniList/MAL ID on the active episode or primary metadata. A different or absent ID rejects it before media capture. A matching ID still cannot override title, season, part, episode, requested Sub/Dub or media-byte conflicts. This adds no provider picker, key or wider provider crawl.

## Verification and limits

Final production Debug build: **zero warnings/errors**. **495 selected C# tests and 122 Python tests passed**, zero failures/skips; eight desktop-input C# classes remain excluded while the user works on the primary monitor. The new tests cover normal-title request count, one fallback within the same site, synonym limits/ranking, native-only titles, missing/matching/wrong IDs, and wrong season/episode rejection.

The final-build scraper used a private profile and cached automatic index. A known same-entry English title was supplied as a synonym to simulate a catalog entry with only `Sousou no Frieren`; the resolver attempted the original and then the fallback on one indexed site. It returned native selected-Dub media with one caption, consistent episode 1, `synonym_match=true` and a provider-observed matching MAL ID in **16.756 seconds**. The source was not newly rendered in VLC or listened to; the media's spoken language remains unverified in this batch. Earlier romanized no-alias failure at 112.807 seconds happened under different network conditions. These samples establish the repaired route, not a general speed or accuracy rate.

Entries without a safe searchable synonym or independent provider ID remain unresolved. Unknown/special unit mapping, broader source outages/cancellation, multi-provider coverage, returned-frame/focus/resource and changed-provider/restart resume remain open, as do temporary downloads and the other media work.

[Final executable](C:/Users/user/animeapp/.artifacts/implementation/anime-synonym-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [selected C# TRX](C:/Users/user/animeapp/.artifacts/implementation/anime-synonym-build/results/anime-synonym-selected.trx), [Python log](C:/Users/user/animeapp/.artifacts/implementation/anime-missing-aliases-20260930/python-final.log). Private source result, service log and isolated browser profile are under the ignored evidence directory; source URLs are not reproduced here.
