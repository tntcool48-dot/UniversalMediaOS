# Archive trailers and superseded revisions are no longer offered as films — October 21, 2026

Part of the open "other providers … exact remakes/cuts" gate. Branch **codex/app-recovery** on **2667087**. Only keyless public-domain metadata was read; no copyrighted remake was streamed. The embed scraper was not used, and no media bytes were downloaded.

## Live probe through the production provider

A temporary test, since deleted, ran `InternetArchiveSourceProvider` against real archive.org for three public-domain remake pairs, with a recording handler. Every response and verdict is retained under `.artifacts/implementation/archive-remake-probe-20261021/` (before the repair, 31 responses) and `archive-remake-probe-after-20261021/` (after).

Before the repair:

- **Night of the Living Dead (1990)** offered **4** candidates, all Turner items titled exactly "Night of the Living Dead", year 1990, in Archive's curated `movie_trailers` collection. Their subjects say "trailer" and they run 66–74 s.
  - One, `turner_video_99799`, has only superseded `history/files/*.~1~` revisions: its current file was removed. The provider still chose `history/files/99799.mp4.~1~` as the media file.
- **House on Haunted Hill (1959)** offered **8**: 3 real films (about 74.7 min) and 5 Turner trailers or a featurette (99–172 s).
- **The Little Shop of Horrors (1960)** offered **6**, including 2 Turner items.

The catalog lists such unverified candidates as "MATCH UNVERIFIED" with **Try stream**, so the 1990 remake page offered a trailer as the film.

## Repair

- **Curated extras collection.** An item in an Archive collection curated for short extras (trailer, teaser, clip, preview, excerpt or featurette, for example `movie_trailers`) is no longer offered for a film or episode.
  - Free-form subject tags are unchanged: they only withhold verification, as four existing `ArchiveMovieFileSelectionTests` cases deliberately record.
  - Longer labels such as "making of" stay file-level only, since they can describe a feature documentary.
- **Superseded revisions.** File selection never uses Archive's `history/…` revisions or `.~N~` files.

## Verification

| Run | Result | TRX |
| --- | --- | --- |
| New `ArchiveSupplementalItemTests` before the repair | **1 / 5**: real trailer and featurette offered, a history revision selected, a history-only item offered. The over-rejection guard (a film titled "Trailer Park Boys") passed. | `archive-supplemental-items-before-20261021.trx` |
| New and all Archive-related classes after the repair (direct identity, file selection, catalog paging, audiovisual service, workflows) | **95 / 95**, 11 s | `archive-supplemental-items-related-20261021.trx` |

A first, broader version also rejected free-form "trailer"/"clip" subjects. It failed those four existing cases and was narrowed to curated collections.

**The same live probe after the repair:**

- *House on Haunted Hill* (1959): **3** candidates, the three full films only.
- *Night of the Living Dead* (1990): **0**.
- *The Little Shop of Horrors* (1960): **4**, including a colorized upload.
- *Night of the Living Dead* (1968): unchanged at **7**, one verified.
- The 1999 and 1986 remakes: still **0**; no other year's item crosses over.

## Decision: keep these unverified

**The user decided on October 21 to keep them unverified ("Try stream").** Curated collection membership is not accepted as verification evidence. The analysis that informed the decision follows.

Most real full-length films stay **unverified**, "Try stream", because their free-form subjects do not say "feature film". This includes all three 1959 films and six of the seven 1968 films. Most sit in Archive's curated `feature_films` collection. Counting that collection as work-form evidence would have verified them, with title and year still exact. It would also have verified altered presentations in that collection, such as the colorized 1960 upload, unless excluded.

## Limits

This is three remake pairs on one provider. Spoken audio, captions, cut length against the catalog's runtime, other providers and catalog DASH remain open.
