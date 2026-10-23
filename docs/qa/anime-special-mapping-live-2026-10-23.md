# Live numbered-special mapping, transfer and lookup — October 23, 2026

## Live discovery exposed a false absence

The production temporary-watch discovery (`TemporaryEpisodeWatchService.FindCandidatesAsync` with the real `DualTrackerRssParser`) ran against live Nyaa for **16 real multi-episode AniList OVA/SPECIAL entries**. It used the same title, alias and synonym list as `AnimeDetailsViewModel`, with each feed query paced 3 seconds apart. **All 16 returned no match.** Some absences were honest: batches, season-0 numbering, `S03OVA02`/`OVA 01` ordinals, cumulative OVA numbers and titles with no labelled single-episode release. Others were false absences. These real releases name the whole work, carry an OVA/Special label and give one explicit in-range unit, but were still rejected:

- `[Tsubaki-Subs] Hellsing Ultimate OVA - 05 [DVD x264 AC3][7E15928E].mkv` (AniList 777, OVA/10)
- `[anon] No Game No Life Specials - 01 [1080p].mkv` and `[Utskushii] No Game No Life Specials - 01 [A9E28304][720p, AAC].mkv` (20769, SPECIAL/6)
- `[DKB] Tensei shitara Slime Datta Ken - Coleus no Yume - E01 (OVA)[1080p][HEVC x265 10bit][Multi-Subs]` (161802, OVA/3)
- `[DKB] Kono Subarashii Sekai ni Shukufuku wo! 3 Bonus Stage - E01 (OVA)[1080p][HEVC x265 10bit][Multi-Subs]` (181244, OVA/2)
- `[BlurayDesuYo] Shingeki no Kyojin - Lost Girls OVA - 01 (DVD 1024x576 AAC 10bit) [489D792C].mkv` (99634, OVA/3)

The numbered-special matcher accepted only `Ep N`/`Episode N` units, only the singular `Special` label, and only a short list of technical tags. The standard ` - 05` and `E01` units, the plural `Specials`, and tags such as `DVD`, `AC3`, `Multi-Subs`, `576p` and `1024x576` made these releases fail.

AnimeTosho's feed host no longer resolves (`No such host is known. (feed.animetosho.org:443)`). Every query that Nyaa answered with no rows therefore ended as typed *incomplete discovery*. Nyaa itself also returned one `502 Bad Gateway`. The source is recorded as unavailable; failed lookups were not repeated to get a green result.

## Repair

`FileMatchesNumberedSpecial`, which runs at the feed, torrent-metadata and sole-video stages, now also accepts:

- a single ` - N` or `E N` unit
- the plural `Specials` label
- source, subtitle and resolution tags (`BD`, `DVD`, `WEB`, `WEBRip`, `WEB-DL`, `CR`, `AC3`, `Multi-Subs`, `NNNp`, `WxH`) as release decoration
- underscores as word separators, as in `[Hatsuyuki-Kaitou]_Shingeki_no_Kyojin_OVA_-_01_[848x480]` and `[DGz]_Hellsing_Ultimate_OVA_-_03_[720p]`

Everything else is unchanged:

- The OVA/Special label is still required. The 1993 *JoJo's Bizarre Adventure* OVA shares its English title with the 2012 TV series, so whole-title equality alone is not identity.
- These remain rejected: `#N` and season-qualified units, decimals, ranges, two units, `OVA N`/`Specials N` ordinals, batches, `TV`/`ONA`/movie labels, and any extra words that name another work.
- The catalog form, known count and in-range episode gates are unchanged.

## Verification

- The new regression cases failed before the repair: **7 failures / 83 passes** in the two special classes, all of them the 7 new expected-match cases. The follow-up resolution-tag cases failed **2 / 37** in their theory before that addition. The underscore case failed **1 / 42** before its one-line addition.
- After the repair, the affected and related scope (special mapping, special identity, temporary-watch, temporary discovery and native temporary-download boundary) passed **148 / 148**. After the resolution-tag addition, the two special classes passed **93 / 93**, and after the underscore addition **97 / 97**. The whole affected and related scope was then run once more on the final matcher: **155 / 155**.
- No UI fixture, local full suite or hosted run was used.
- Evidence (before/after TRX files and the probe reports) is under `.artifacts/implementation/anime-special-mapping-20261023`.

Repeating the live discovery with the repaired code now returns the exact release for **4 of 5** targets:

| Target | Accepted release | Seeders |
| --- | --- | --- |
| Coleus E01 | `[DKB] ... Coleus no Yume - E01 (OVA)` | 1 |
| KONOSUBA Bonus Stage E01 | `[DKB] ... 3 Bonus Stage - E01 (OVA)` | 2 |
| No Game No Life Specials 01 | `[anon]`/`[Utskushii] No Game No Life Specials - 01` | 0 |
| Hellsing Ultimate 05 | `[Tsubaki-Subs] Hellsing Ultimate OVA - 05` | 0 |

No wrong episode or work was accepted. The fifth target, Lost Girls 01, was then re-run after the resolution-tag addition. It accepted exactly `[BlurayDesuYo] Shingeki no Kyojin - Lost Girls OVA - 01 (DVD 1024x576 AAC 10bit)` (7 seeders) and `[AoTs] ... Lost Girls OVA - 01` (0 seeders). Two releases in the same results stayed out: the unlabelled `[O-IM] Shingeki no Kyojin Lost Girls - 01` and the `16.5 ... OVA - 1 [ENGSUB].ass` subtitle file. A repaired re-run of *Attack on Titan OVA* (18397, OVA/3) episode 1 saw all of these in one result set. It accepted only `[DeadFish] Shingeki no Kyojin - 01 - OVA [DVD][480p][AAC].mp4`, which matches its romaji title *Shingeki no Kyojin OVA*. It rejected both Lost Girls episode-1 releases, `Kuinaki Sentaku - 01 - OVA`, every batch/range and the underscore-separated `Shingeki_no_Kyojin_OVA_-_01` files. *No Regrets* (20811) episode 1 remains honestly unmatched: its only labelled single release omits the catalog's `Gaiden`.

## Ordinary episodes no longer take a plural-labelled special

Reviewing the change exposed a wrong-work acceptance in the ordinary (TV) feed filter, which existed before this batch. Its special exclusion matched only singular `movie/ova/ona/special`. A TV request for *No Game No Life* episode 1 therefore accepted `[anon] No Game No Life Specials - 01 [1080p].mkv`, a live release of the separate SPECIAL entry 20769, and also `OVAs`, `OAD` and `Movies` forms. The new regression failed **4 / 5** (only the plain `- 01` control passed). The filter now also excludes `movies`, `ovas`, `onas`, `oad(s)` and `specials`. The affected and related download scope then passed **173 / 173**, including the existing download-routing regressions.

## Bounded real transfers through the production pipeline

The production `TemporaryEpisodeWatchService.DownloadAsync` ran live against Nyaa and real peers. It used an owned scratch data root and a 25-minute cap, and held the shared desktop lock in case of a firewall prompt. No app window was opened.

**Attack on Titan: Lost Girls, episode 1 (Sub): honestly unavailable.** The run took 64 seconds and transferred no payload.
- `[BlurayDesuYo] ... Lost Girls OVA - 01` (7 seeders) was rejected at the torrent-metadata stage. Its metadata names `Shingeki no Kyou`**`jin`** while the feed and catalog say `Kyojin`. A romanization difference is not whole-title evidence, and no transliteration was added.
- `[AoTs] ... Lost Girls OVA - 01` has an exact single-file name (391,512,217 bytes) but no peers. Its metadata timed out at the existing 60-second limit.
- Both owned job folders were removed. The result was the existing *No matching episode could be downloaded and verified* error.

The metadata for `[DKB] ... Coleus no Yume - E01 (OVA)` drops the `(OVA)` label (`... - E01 [1080p][HEVC x265 10bit][Multi-Subs].mkv`), so that release stays rejected at the metadata stage by the unchanged label rule.
