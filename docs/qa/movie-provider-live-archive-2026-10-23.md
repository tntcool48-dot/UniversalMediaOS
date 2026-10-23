# Live Movie providers: Archive titles, mislabeled files, cuts and captions — October 23, 2026

Part of the open "other providers … exact remakes/cuts, audio, full captions and sustained native availability" gate. Branch **claude/movie-tv-providers-c8a6db**, cut from **e744dcd**. Only public-domain or freely licensed films were probed. Archive and Wikidata were read keylessly (metadata only); the Vidsrc probe used IMDb IDs of public-domain films and one remake pair.

## Live probe through the production catalog and resolver

A temporary test (not committed) ran production `WikidataMetadataClient` search for 30 public-domain films and remake pairs. It then sent each exact catalog identity through production `InternetArchiveSourceProvider`. For every candidate it recorded:
- verification status;
- the selected file and its Archive `length` against the catalog runtime (Wikidata P2047);
- every video and caption file on the item.

Responses, verdicts and both runs are under `C:\Users\user\animeapp\.artifacts\implementation\movie-tv-providers-20261023\` (`archive-runtime-before/`, `archive-runtime-after/`). Wikidata rate-limited this shared machine (HTTP 429, retry-after 31 s), so the probe paces its catalog requests.

Two concrete faults surfaced.

### 1. Uploads titled with their own year never matched

Archive uploaders very often title an item `Title (1964)`, `Title [1950]` or `Title 1964`. Exact title matching compared "the last man on earth 1964" with "the last man on earth", so these were dropped. Examples from the first run:
- *The Last Man on Earth* (1964) showed **no** Archive source; Archive returned 14 items, including six full uploads titled this way.
- *Plan 9 from Outer Space* (catalog 1957) showed none; three uploads are titled `… (1957)` or `… 1957`.
- *Dementia 13* (1963) showed none; two uploads are titled `Dementia 13 (1963)`.
- *D.O.A.* (1950) showed none; the one upload is `D.O.A. [1950]`.
- *A Star Is Born* (1937) showed **1** candidate of 9 full uploads.

**Repair.** When an Archive item's title ends with the item's own year, the title without that suffix becomes an alternate title for exact matching. The original title is kept. A suffix that is not the item's own year stays part of the title. A title that is only a year (*1984*) is kept whole. Trailers such as `Dementia 13 [1963] - Trailer` still do not match.

### 2. A file named for another film was offered as the film

*His Girl Friday* (1940) offered the Archive item `GirlFriday`. Its metadata describes the 1940 film (title, year, IMDb tt0032599, "92 min"). Its only film file is `The_Saint's_Girl_Friday_1953.mp4`, which runs 64.5 minutes: a different film.

**Repair.** A file whose own name pairs a year other than the item's with title words that do not contain any of the item's titles declares another film, and is no longer selected. Other files in the same item remain eligible. These still pass:
- a re-release or restoration year beside the item's title (`Reefer_Madness_1938.mp4` in the 1936 item, `Metropolis_2010_restoration.mp4`);
- a title that contains a year (`2001_A_Space_Odyssey.mp4`);
- a bare upload number (`1900.mp4`).

The probe checked every video file of the matched items: only `GirlFriday` and the Turner trailer number `1900` carried a conflicting year. The trailer was already excluded by its collection.

## Verification

| Run | Result | TRX |
| --- | --- | --- |
| New `ArchiveYearSuffixedTitleTests` before repair 1 | **4 failed / 3 guards passed** | `trx/archive-year-suffix-before-20261023.trx` |
| Repair 1 and Archive-related classes (direct identity, file selection, supplemental items, audiovisual service, catalog paging) | **87 / 87**, 13 s | `trx/archive-year-suffix-related-20261023.trx` |
| New `ArchiveForeignFilmFileTests` before repair 2 | **2 failed / 6 guards passed** | `trx/archive-foreign-file-before-20261023.trx` |
| Repair 2 and the same classes | **95 / 95**, 15 s | `trx/archive-foreign-file-related-20261023.trx` |

The TRX paths are relative to the evidence folder above. The fixtures are trimmed from the live responses, not synthetic shapes.

**The same live probe after both repairs:**
- Candidates rose from **57 to 140**, and verified candidates from **2 to 6**.
- Catalog films with no Archive source fell from **14 to 7**:
  - *The Last Man on Earth* 0 → 6;
  - *Plan 9* 0 → 3;
  - *Dementia 13* 0 → 2;
  - *D.O.A.* (1950) 0 → 1 and (1988) 0 → 1;
  - *A Star Is Born* (1937) 1 → 6 and (1954) 0 → 1.
- The *Saint's Girl Friday* file is gone; *His Girl Friday* now has nine genuine uploads (two verified), all 91.7–92.6 minutes.
- No remake crossed years. The 1954, 1976 and 2018 *A Star Is Born*, the 1990 *Night of the Living Dead* and the 1998 *Carnival of Souls* still never receive another year's upload.

## Cuts and runtime (observed, not repaired)

Archive's file `length` lets the probe compare each selected file with the catalog runtime. Five of the six verified candidates match closely: *Night of the Living Dead* 95.9 vs 96 min, both *His Girl Friday* uploads 92 vs 90, and *Sintel* 888 s. The sixth is short. A verified *Hunchback of Notre Dame* (1923) upload runs **109.2 min against the catalog's 133**: a surviving shorter print, verified by title, year and its "feature film" subject. Unverified ("Try stream") uploads are often other cuts:
- *The Lost World* (1925): 68 / 76 / 92 / 104 min against 104 (abridged Kodascope prints beside restorations).
- *The General* (1926): 75.9–106.8 min against 75.
- *The Brain That Wouldn't Die*: 71.1 vs 82.
- Silent films vary with projection speed (*Nosferatu* 79–95 against 94).

Runtime is not evidence the matcher uses, so the *Hunchback* print stays verified as the film. Withholding verification for a shorter cut, or showing a cut/runtime label on the source card, is open. Silent-film speed and surviving prints make a fixed threshold unreliable.

## Captions (observed, a product decision)

Many matched items carry sidecar subtitles that the Archive provider never passes to the player:
- mostly Archive's machine speech-recognition `*.asr.srt` (17 of the 21 items with captions, first run);
- release subtitles timed to other cuts: *Metropolis* has seven, for the 2010 restoration, the 1927 Blu-ray and a 24 fps remaster.

Offering them needs a decision on machine captions, plus language and cut-timing checks. Embedded tracks in the selected file are unaffected.

## Catalog year (observed)

*House on Haunted Hill* has four normal-rank Wikidata release dates (1958-12-17 to 1959-10-27). The catalog uses the earliest (1958), while Archive dates its uploads 1959, so the film still shows no Archive source. *Plan 9 from Outer Space* has a preferred-rank 1959 date, but the catalog shows 1957. Changing the catalog-year rule affects identity display and other providers; it is recorded here, not repaired.

## Limits

This is one provider (Archive) against a finite public-domain sample. The Vidsrc probe, physical native playback, spoken audio, configured providers and catalog DASH follow below or remain open.
