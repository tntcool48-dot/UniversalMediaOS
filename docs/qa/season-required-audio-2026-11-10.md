# Season downloads with a required audio language — November 10, 2026

Merged source **401155b** (`codex/app-recovery`), Debug build in the worktree's own `.artifacts/implementation/build`. Follows [catalog DASH and TV seasons](catalog-dash-and-tv-season-2026-10-23.md).

## Merge check

The merged build compiled with zero warnings and zero errors. `LongLocalMediaPathTests` (the October 23 long-path repair, now combined with download-captions' `CaptionLanguage.FromFileName` in `PlaybackViewModel.LoadMedia`) passed **4 / 4**.

## The problem

TV Shows → Download season takes the **Preferred language** choice as a required spoken language. Source selection was already strict. `ExactAudiovisualMatcher` treats a source with no audio declaration as unknown, and one declaring another language as a conflict, so neither was ever used or saved. The season flow around it was wrong:

- It stopped at the **first** episode that had no source declaring the language.
- It never looked up the remaining episodes.
- Its summary ("Stopped at S01E02 … 1/4 completed episodes are kept") did not say which episodes were unavailable in the requested language.

The availability of the rest of the season was therefore unknown, and the summary counted nothing as missing.

## Failing check, repair, checks

`SeasonLibraryDownloadTests.RequiredAudioSavesOnlyDeclaredEpisodesAndCountsTheRestAsUnavailable` uses a four-episode season with English required:

| Episode | Source's audio declaration |
|---|---|
| E1 | English |
| E2 | none |
| E3 | Spanish |
| E4 | English |

It expects:

- all four looked up;
- transfers only for E1 and E4;
- both saved files' `AudioEvidence` containing `en`;
- a summary that reads "Saved 2 of 4 …" and names S01E02 and S01E03 as not available with English audio.

**Before the repair: 1 failed, 0 passed.** Lookups stopped at [1, 2]; E2 was already not saved.

The repair is in `AudiovisualCatalogViewModel.DownloadSeasonAsync`, commit **68dde1e**:

- **Required language:** when an episode's first lookup finds no verified native source that declares the language, the episode is recorded as unavailable in it, a warning is logged, and the season moves on to the next episode.
- **Summary and toast:** both now read "Saved X of N episodes from Season S with English audio … K not available with English audio: S01E02, S01E03." The percentage shows the saved share. A later stop or cancel also names the episodes already found unavailable.
- **Unchanged:**
  - Without a chosen language, the first missing episode still stops the season (October 3 design).
  - Transfer failures still try at most three verified sources and then stop.
  - Saved files with unknown audio still cannot satisfy a required language.
  - Nothing is ever saved in another or an undeclared language.

**After the repair:**

- **44 / 44** `SeasonLibraryDownloadTests` pass (43 existing plus the new check).
- **61 / 61** related catalog view-model checks pass (`AudiovisualPlaybackProgressTests`, `CatalogViewModelPagingTests`); [TRX](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/required-audio-related-20261110.trx).
- Zero warnings, zero errors. No live provider or real-app run was needed for this repair.

## What this means with today's providers

The real scraper's item evidence for both qualified seasons declares subtitles but no spoken audio: Breaking Bad S1 and The Office S1 were saved with `AudioEvidence` null. Internet Archive's item evidence declares no audio either; it keeps the item's `language` field only as a source label. Requesting a specific language for these seasons therefore now ends honestly: no files, and every episode listed as not available with that audio. It no longer stops after the first episode. A language-specific season download cannot succeed until a provider declares audio for its episodes.

## Still open

- Spoken audio for Breaking Bad S1 and The Office S1 is still unknown. Automated speech-language samples are deferred to a later turn, and the human listen stays with the user.
- No real season has yet been saved with a declared spoken language.
- **Product decision taken the safe way:** with a required language, episodes are reported as unavailable and skipped, rather than saved with unknown audio. Accepting undeclared audio would need the user's decision.
