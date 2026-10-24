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

## Visible Movie journey, catalog to Downloads (second monitor, isolated profile)

Driven with real mouse clicks and keys through UI Automation on the second monitor (window 1936×1048 at 2552, 354), against the worktree's Debug build of **dbec43f**, with services auto-management off and a profile under the worktree's `.artifacts/qa/download-captions-20261023/movie-profile`. Screenshots are in that folder's `evidence/`.

1. **Movies → "Sintel" → Search** found one Wikidata card, Sintel (2010). **Find sources** returned "2 verified, 2 unverified candidates: 4 native stream options, 0 website fallbacks": the verified Archive item (CC BY-ND 4.0, `sintel_202207`), a verified Vidsrc HLS source and two unverified Archive items.
2. **Download to library** on the Archive card showed the fixed progress row with Cancel (6 % after 20 s, then 19/32/52/69/93 %) and finished at 23:11:28, 12 minutes after the click: "Saved video and 0 caption file(s) to …\Movies\Sintel-8c945d56ddac2fe3\Feature-fbf0fffb\media.mkv". The file is 1,172,428,172 bytes, equal to the Archive original. Zero caption files is correct: this source's ten captions are embedded.
3. **Downloads** listed "Sintel" with Delete and Play. **Play** opened native playback; Space paused at 5.8 s.
4. **Playback options → Subtitle track** listed **CC Off, German, English, Spanish, French, Italian, Dutch, Polish, Portuguese, Russian, Vietnamese**, in file order, with English chosen by LibVLC.
5. Choosing **German** with the mouse logged "Prepared the selected embedded text caption for independent native seeking" (the step `ger` skipped before dbec43f). A slider click to **120.957 s**, inside the 118.0–121.45 s cue that starts before the 120.042 s keyframe, showed the German two-line cue while paused. **Russian** (Cyrillic) and **Vietnamese** (stacked diacritics such as ậ, ố, ẩ) then rendered at the same paused position, each prepared on choice.
6. **CC** off removed the Vietnamese cue and the transport dropped back; **CC** on restored Vietnamese. **Downloads → Sintel** returned paused at **120.881 s** with the cue (76 ms of paused redraws).
7. **Watch via download** on the verified Vidsrc card (23:18:43) saved a 58,418,393-byte local video and **eleven provider caption files** and opened a second player tab at the film's saved position. The menu offered **eleven identical "Downloaded subtitles" choices** (fault 3 below). Closing that tab, its only owner, emptied the temporary-watch folder by 23:25:21.

**Fault 4 observed here: the menu reorders itself.** After English (on the first seek) and then German were prepared, the menu read "CC Off, Spanish, French, …, Vietnamese, English, German"; each prepared language moved to the end, because its seek-safe copy registers as the newest native track. With ten languages, the list reshuffles as you use it.

**Outside input during the run.** At 23:15:54 the Sintel tab moved left twice and the player skipped −10 s, with no input from the driver. The app's Ctrl+Shift+Left moves the active tab while it can; once the tab reached the edge, the next Ctrl+Shift+Left fell through to the player, whose Left shortcut ignores modifiers. So someone or something on the shared desktop pressed Ctrl+Shift+Left three times while the app had the foreground. That fall-through is noted for the player-shortcut owner, not changed here. The next click on a tab only reactivated the window ("app window activated"), and a second click switched tabs.

## Fault 3: provider captions declared as "Swedish - full"

The Vidsrc scraper result for Sintel (`resolve-verified`, run once from the command line with the same profile) declares each caption with the same value as label and language: "English full", "Swedish - full", "Spanish - full", "Portuguese - full", "Norwegian - full", "Italian - full", "German - full", "French - full", "Finnish - full", "Dutch - full", "Danish - full". dbec43f accepted only a code or an exact language name, so all eleven were saved as `caption-NN.srt` and labelled "Downloaded subtitles".

**Repair (2d926ec).** A provider declaration counts when its leading word is a language name ("Swedish - full", "English [CC]", "Español (Latinoamérica)"); a leading code alone does not, so "No subtitles" is not Norwegian. The label is read when the declared language names none. The regression `PlaylistMediaDownloadTests.SavedCaptionsReadAProviderLanguageNameWithAQualifier` uses the real declarations plus "Deutsch" as a label only, "No subtitles" and "Track 3"; it failed before the repair (`provider-language-names-before-repair-20261023.trx`), and the 151-case download/caption scope passes after it (`provider-language-names-after-repair-20261023.trx`).

On the repaired build the same Watch via download saved `caption-01.en.vtt`, `caption-02.sv.srt`, `caption-03.es.srt`, `caption-04.pt.srt`, `caption-05.no.srt`, `caption-06.it.srt`, `caption-07.de.srt`, `caption-08.fr.srt`, `caption-09.fi.srt`, `caption-10.nl.srt`, `caption-11.da.srt`. The menu listed "English (downloaded)" through "Danish (downloaded)" in the provider's order, and Swedish and Finnish rendered at the 1:48 cue while paused. Each external-caption choice reloads the input and advanced the paused position about 0.42 s (108.500 → 108.939 → 109.357 s), as recorded on October 5.

The provider's Swedish and Danish files are plain ASCII with the å/ä/ö/ø letters already removed ("Detta vapen har en mrk historia"), and its Finnish file is in a dialect. That is the provider's content, not a decoding fault: the bytes are saved unchanged.

## Fault 4 repair: menu order

**Repair (5581330).** A prepared copy now takes its original track's place in the menu; a copy whose original is gone still follows the list. `NativeShortMediaCompletionTests.PreparedEmbeddedCaptionsKeepTheirPlaceInTheMenu` encodes a real two-track MKV (`ger`, then `eng`), chooses each in turn and checks the menu order. Before the repair it read "CC Off, English, German" (`caption-order-before-repair-20261023.trx`); after it, it and the three embedded pixel cases pass 4 / 4 in three runs, and the 74-case native/caption scope passes (`caption-order-related-20261023.trx`).

**Audio left on.** The Vidsrc player tab opened at full volume while its overlay controls were hidden from UI Automation, so my mute missed and the film played audibly on the shared desktop for about nine minutes (23:27–23:36) before it was paused. A new player tab does not take the first tab's volume.
