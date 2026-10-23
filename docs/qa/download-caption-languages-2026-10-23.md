# Caption languages, selectors and visible Movie/TV download journeys — October 23, 2026

Item: the first open bullet under "Watch via download" in the [checklist](../IMPLEMENTATION_CHECKLIST.md). Branch **claude/download-captions-7abd2a** from **e744dcd**.

## Real material

- **Sintel (2010)**, Blender Foundation, CC BY (Archive item `sintel_202207`, declared CC BY-ND 4.0). Its original `data/sintel.mkv` (1,172,428,172 bytes) carries **ten embedded SubRip tracks**, in this order: German `ger`, English `eng`, Spanish `spa`, French `fre`, Italian `ita`, Dutch `dut`, Polish `pol`, Portuguese `por`, Russian `rus` (Cyrillic) and Vietnamese `vie` (Latin with stacked diacritics). It is the catalog's verified Archive source for the film.
- **Arabic and Japanese captions for Sintel** from Wikimedia Commons (`TimedText:Sintel movie - Blender Fondation.ogv.ar.srt` / `.ja.srt`, CC BY-SA), 1,752 and 1,640 bytes, aligned to the film (first cue 1:47.250). No catalog provider supplied Arabic captions for this film; these files stand in for a provider's Arabic caption once saved beside a download. The Commons "en" page is vandalized (Romanian text, broken timings) and was not used.
- **Breaking Bad S1** (seven saved episodes with English VTT captions) from the October 9 season QA, reused read-only by hardlink as the earlier QA profiles already do. Not freely licensed; reused because it is already on disk.

## Two faults in how real caption languages reach the selector

**1. ISO 639-2/B track tags lost their language.** Matroska commonly tags tracks with bibliographic codes; Sintel's German, French and Dutch tracks are `ger`, `fre`, `dut`. The player named them correctly (ICU resolves the names), but kept no language for them: their key was `Subtitle:ger|` rather than `Subtitle:de|`, and their language was empty. An empty language skips the seek-safe derived caption (the October 12 repair that makes a cue starting before the video keyframe render after a paused seek), and a remembered choice or audio preference cannot follow the track by language. The derived-caption code itself already knew `ger/fre/dut`; only the player's gate did not. The same loss applies to the other 17 bibliographic codes (`chi`, `cze`, `gre`, `per`, `rum`, …).

**2. Saved caption files kept a language only for English and Arabic.** A provider caption list is saved beside the video as `caption-NN<.lang>.<ext>`, and the player reads the language back from that name. Only `en/eng` and `ar/ara` were written and read, so French, Spanish, Japanese or Portuguese captions all appeared as identical "Downloaded subtitles" choices, and a choice could not follow its language to the next episode.

**Repair (dbec43f).** A shared `CaptionLanguage` helper in Core maps the twenty bibliographic codes to their terminology forms before the culture lookup, names a saved caption by any known language (`caption-02.fr.vtt`, `caption-04.pt-br.vtt`; an unknown or empty tag keeps no segment), and reads a caption file's last name segment back as its language (`.fr`, `.fre`, `.French`), ignoring segments that are no language (`.final`). The player labels such a file "French (downloaded)" and keys it by language. English and Arabic names and labels are unchanged; the track option, the derived-caption track match and the HLS caption saver all use the helper.

## Regressions

All in `UniversalMediaOS.Tests.E2E`, against an isolated Debug build in the worktree's `.artifacts/implementation/build`.

- `LocalEmbeddedCaptionPreparationTests.BibliographicTrackTagsKeepTheirLanguage` (8 tags): `ger/fre/dut/chi/cze/gre/per/rum` keep the two-letter language, the same key as the two-letter tag, and their English name.
- `NativeShortMediaCompletionTests.PausedEmbeddedCaptionRestorationRepaintsWithoutStartingPlayback(srt, ger, German)`: the existing real-decode pixel case, now also with a `ger`-tagged track. LibVLC enables an English-tagged track by itself but not a German one, so this variant enables the track in the decoder and rewinds to the first cue; every later assertion is unchanged.
- `PlaylistMediaDownloadTests.SavedCaptionsKeepEveryDeclaredLanguageInTheirNames`: eight declared tags (`en, fre, es, pt-BR, ara, ja, "", not-a-language`) are saved as `.en/.fr/.es/.pt-br/.ar/.ja` and two without a segment.
- `SubtitleHandoffTests.SavedCaptionFilesShowTheirDeclaredLanguagesAndKeepTheChoiceAcrossEpisodes`: eight saved files show eight labels (two unknown ones stay "Downloaded subtitles"); French attaches only its own file, and the next episode's French file is chosen although its files are saved in another order and tagged `fre`.

**Before the repair:** 11 of the new cases failed (`captions-before-repair-20261023.trx`): all 8 tags (language empty), the naming case, the label case, and the German real-decode case. The first German version failed at its opening wait, because LibVLC does not enable a German-only track; after the rewind change it failed exactly at the derived-caption check (`offDuringRegistration` 0, expected 1: preparation never started) with only the track-option repair reverted (`embedded-ger-before-repair-20261023.trx`). The first rewind-less German run with the repair paused at 3.771 s, between the fixture's two cues, which is why the variant rewinds.

**After:** the three embedded-caption cases pass 3 / 3 in three runs, and the affected/related scope (`SubtitleHandoffTests`, `LocalEmbeddedCaptionPreparationTests`, `PlaylistMediaDownloadTests`, `NativeAudioPreferenceMatchingTests`, `TemporaryWatchDiscoveryTests`, `SeasonLibraryDownloadTests`, `TemporaryMediaWatchTests`, the embedded-caption and anime audio-preference native cases) passes **156 / 156** (`caption-languages-final-20261023.trx`).
