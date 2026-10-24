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

**KONOSUBA 3 BONUS STAGE (AniList 181244, OVA/2), episode 1 (Sub): completed.** The firewall now has inbound Allow rules for `dotnet.exe`, so this run raised no prompt and opened no window, and it ran without the desktop lock.
- **Accepted release:** `[DKB] Kono Subarashii Sekai ni Shukufuku wo! 3 Bonus Stage - E01 (OVA)[1080p][HEVC x265 10bit][Multi-Subs]`, 2 seeders.
- **Metadata:** the torrent name keeps the whole romaji title, the `(OVA)` label and `E01`. It lists one video, so the sole-video rule selected it and only that file was prioritised (`Downloading only episode 1 (552 MiB)`).
- **Transfer:** 20:27:46 → 20:35:01 UTC, **438 seconds**. The completed file is **579,171,988 bytes**, equal to its metadata length. SHA-256 `F7D434A60F2FCA081FC0909B0EAD440266FA1D733DFC47EEB8D4F7BA1F0AB99F`.
- **Production checks:** the existing ffprobe verification passed (video, audio, selectable subtitles, so no caption notice), and the file was published to the owned job as `episode-0001.mkv` under the catalog unit, not the provider label.
- **Independent ffprobe:** 1920×1080 HEVC, **1,470.078 s**, AAC tagged `jpn` "Japanese", and five ASS subtitle tracks (`eng` English, `spa`, `por`, `ara`, `rus`). These tags do not prove spoken Japanese or rendered English cues.
- **Cleanup:** releasing the final lease deleted the owned job folder (`job folder exists=False`), so no test media remains.

This qualifies one real numbered multi-episode OVA transfer through the production feed → metadata → sole-video → verification → publish → release pipeline. It does not qualify in-app native playback, physical controls, captions or spoken audio for this file. No visible app journey was run for it.

## Streaming lookup for special units

The production `scraper.py resolve` ran with exactly the arguments `ScraperEngine.ResolveAsync` passes: English title, `[English, romaji]` aliases, AniList/MAL IDs, up to 8 synonyms, 6 sites, the unchanged 63-second budget, and native mode. It used an owned data root and held the shared desktop lock (off-screen Chromium). Scraper SHA-256 before the repair below: `094a2759…712bf`.

| Request | Elapsed | Result | What the providers showed |
| --- | --- | --- | --- |
| Kaguya-sama: Love Is War -Stairway to Adulthood- (194884, SPECIAL/2), ep 1 | 66.969 s | `all_indexed_sites_failed`, 3 sites | Anikoto: identity `consistent`, `episodes=[1]`, `catalog_id_match=True`; selected host reported the file unavailable |
| same entry, ep 2 | 66.109 s | `all_indexed_sites_failed`, 3 sites | Anikoto: `consistent`, `episodes=[2]`, ID match; host unavailable |
| Attack on Titan: Lost Girls (99634, OVA/3), ep 1 | 67.484 s | `all_indexed_sites_failed`, 3 sites | Anikoto: `consistent`, ID match; host unavailable |
| alias-free/ID-free `かぐや様は告らせたい 大人への階段`, ep 1 | 65.485 s | `all_indexed_sites_failed`, 4 sites | Native discovery found one exact AniList entry (2 translated titles); Anikoto: `consistent`, `catalog_alias=True`, ID match; host unavailable |

So the special-unit mapping itself works on the streaming side: both units of a two-episode SPECIAL, an OVA unit and an alias-free native request each reached an independently consistent provider page with a matching catalog ID. Every attempt then ended in the provider's selected host reporting the file unavailable, so **no accepted live player was reached**. That outcome is recorded as provider unavailability, not as a lookup success. No website opened automatically, and the budget and site limits were unchanged.

### Fault: a parent series page could stand in for its special

The same logs show the parent series being offered for a special:
- Re:Anime returned `/anime/kaguya-sama-love-is-war-5d97k2/ep-1` (the TV series) for the Stairway special.
- For Lost Girls, Re:Anime returned `/anime/attack-on-titan-p9y2p9`, and Miruro's `/watch/…/attack-on-titan` page was judged **`consistent episodes=[1]`** with no catalog-ID match.

Only the provider outage stopped a capture of the parent's episode 1. Non-native requests proceed on `consistent` or `unknown`, and the primary title's keyword rule accepted any page sharing two keywords and the franchise prefix. The native route already rejected this: its whole-title rule made Re:Anime's parent page a `conflict` in the alias-free request.

**Repair:** when the catalog title ends in a delimited subtitle (`Franchise: Subtitle`, `Title -Subtitle-`, `Title - Subtitle`), candidate scoring and the keyword identity path now require at least one keyword that only that subtitle adds (`stairway`/`adulthood`, `lost`/`girls`, `regrets`).
- A matching provider catalog ID still accepts a shortened heading.
- A conflicting ID still conflicts.
- Season/part-qualified subtitles keep the existing season-evidence rule, so `Mushoku Tensei` for `... Season 3` stays `unknown`.
- Native, alias and whole-title paths are unchanged.

**Verification:** the new module failed **3 of 5** before the repair (a parent card scored 12; both parent pages were `consistent`). After the repair, all **235** Python tests passed, including the 6 new cases.

**Live re-check (scraper `21c7c8c7…`, same limits, desktop lock held).** Lost Girls episode 1 and Stairway episode 1 again ended `all_indexed_sites_failed` (66.859 s and 65.453 s), because Anikoto's consistent, ID-matched special pages had unavailable hosts. Every parent page reached was now a **`conflict`**:
- Miruro `attack-on-titan-season` (both URLs)
- Re:Anime `attack-on-titan-final-season`
- Re:Anime `kaguya-sama-love-is-war-5d97k2`

Re:Anime's parent page was still being *tried*. Its live search HTML for the Stairway query lists only unrelated and parent cards, and the slug fallback synthesized `/watch/kaguya-sama-love-is-war-5d97k2` from shared franchise keywords. A regression built from that card shape failed (the same two URLs). The fallback now applies the same subtitle rule: **236 / 236** Python tests pass, and the live Re:Anime HTML now yields **no candidate** for the special. Re:Anime simply doesn't list it, and that absence is honest.

**Limit:** a provider page that names only a franchise for a TV entry whose catalog title carries an English subtitle (for example `Demon Slayer` for `Demon Slayer: Kimetsu no Yaiba`) is now rejected unless it exposes the matching catalog ID. That is a deliberate false negative rather than a possible wrong work.

## Live cancellation and provider failure

The production `ScraperEngine` (with `PythonBootstrapper`) resolved the Stairway special episode 1 in an owned data root, and its token was cancelled at **20.409 s**. At that moment Anikoto's identity was consistent and its host had just reported the file unavailable, so the live provider failure and the cancellation overlapped. The engine threw `OperationCanceledException` at **20.533 s**. Owned processes (those whose command line names the owned data root: the Python scraper plus its Chromium tree) went from **11 → 0** within 3 seconds. Only a 12 KB browser-profile folder remained; the existing 12-hour stale-profile cleanup covers it. Download-side live failures were also seen: AnimeTosho's DNS failure and a Nyaa `502`. Both ended as typed incomplete discovery, never as a reported absence.

## Unlabelled specials proven unique by AniList (October 24, user decision)

Releases such as `[SubsPlease] Kaguya-sama wa Kokurasetai - Otona e no Kaidan - 01 (1080p)` name the whole SPECIAL with one explicit unit but carry no OVA/Special label. The label rule exists because the 1993 *JoJo's Bizarre Adventure* OVA shares its English title with the 2012 TV series. The user decided: **"Yes, if AniList-unique."**

**Implementation (numbered multi-episode SPECIAL/OVA downloads only).**
- An unlabelled release is considered only if, after its single unit and leading group tag are removed, its whole title equals one catalog title plus known decorations.
- That title then needs **one AniList search** (5 seconds, 256 KiB, `perPage: 50`). The page must be complete (`hasNextPage: false`), and exactly one entry, the requested catalog ID, may carry that whole title in its romaji, English, native or synonym fields.
- Catalog qualifiers such as `(TV)` or `(2011)` are removed before comparing, because releases omit them, so the qualified entry still makes the bare title ambiguous.
- Non-success statuses (including 429), timeouts, HTTP errors, malformed JSON, `errors` responses and incomplete pages all leave the release unmatched. Caller cancellation stays cancellation.
- At most two distinct titles are checked per discovery. The proven titles are passed to the metadata and sole-video stages; nothing is assumed there.
- Everything else is unchanged:
  - catalog form, known count and in-range unit
  - one explicit unit, with no `#`, season, decimal or range
  - batch, `TV`/`ONA`/movie and ordinal exclusions
  - extra words naming another work
  - parent-series names, which never reach the AniList check because they don't equal the whole title
- TV entries keep their existing matching and never ask AniList.
- The internal test constructor has no checker, so existing tests stay offline. The production DI constructor uses the configured `AniListUrl`.

**Verification.**
- The new `SpecialEpisodeTitleUniquenessTests` failed **5 of 21** with stub signatures: exactly the accept-when-unique, proven-title metadata/video, valid-page and caller-cancellation cases. The non-unique and guard cases already held.
- After implementation: **21 / 21**.
- The first live run then accepted `[DeadFish] JoJo no Kimyou na Bouken - 01`, the 2012 TV series, for the 1993 OVA (666). AniList had given the bare romaji to 666 alone, because the TV entry's romaji is `JoJo no Kimyou na Bouken (TV)`. Two new qualifier cases reproduced this (**2 / 23** failing) before the qualifier repair.
- The final affected and related download scope passes **196 / 196**.

**Live discovery with the production AniList check:**

| Request | Proven unique | Accepted releases |
| --- | --- | --- |
| Stairway special (194884) ep 1 | `Kaguya-sama wa Kokurasetai: Otona e no Kaidan` | `[SubsPlease]` 1080p/720p/480p, `[ASW]`, `[Erai-raws]` |
| Coleus OVA (161802) ep 1 | `Tensei Shitara Slime Datta Ken: Coleus no Yume` | unlabelled `[ASW]`, `[New-raws]`, plus the labelled `[DKB]` |
| 1993 JoJo OVA (666) ep 1 | **not proven** | none: the TV release stays unmatched |

**Bounded live transfer (Stairway special ep 1, production pipeline with the AniList check, 25-minute cap, owned scratch root): honestly unavailable.** AniList proved the romaji title at 11:04:01 UTC.
- `[SubsPlease] ... (1080p)` (147 listed seeders): metadata timed out at the existing 60-second limit.
- `[ASW] ... - 01` (368 MiB) and `[Erai-raws] ... - 01` (615 MiB) both passed the metadata and sole-video checks with the proven unlabelled title. Their payloads then made no progress and each ended at the existing 8-minute stall limit.
- `[SubsPlease] ... (720p)` (758 MiB) was still at zero progress when the cap cancelled the request (1,502 s, `TaskCanceledException`).
- The owned `TemporaryWatch` folder was empty afterwards, and no media remained.

This qualifies the proven title through live feed, metadata and sole-video selection, not a completed unlabelled transfer. The earlier KONOSUBA run remains the completed numbered-OVA transfer.
