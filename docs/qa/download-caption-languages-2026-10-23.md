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

## Fault 5: an embedded English track shown under a saved caption choice

For Arabic and other scripts, a library item in its own profile (`arabic-profile`) holds a hardlink to the retained Sintel MKV and the two Commons files saved the way a download names provider captions (`caption-01.ar.srt`, `caption-02.ja.srt`). With saved files present, the menu lists only them: **CC Off, Arabic (downloaded), Japanese (downloaded)**.

On the 2d926ec build, at the 1:48 cue, the menu showed "Arabic (downloaded)" and then "Japanese (downloaded)", but the frame showed Sintel's **embedded English** cue ("This blade has a dark past.") under both. Every native input asked LibVLC for `:sub-language=eng,en`, and that preference outranks the attached caption. Pure sidecar files are unaffected: HLS downloads keep only video and audio, and the Pilot has no embedded captions. Anime torrents whose videos carry embedded subtitles beside an external `.ar.srt` or `.ass` file would show the same mismatch.

**Repair (71d20c1).** The English preference is set only when no saved or provider caption is attached. `NativeShortMediaCompletionTests.SavedCaptionChoiceShowsInsteadOfAnEmbeddedTrack` encodes an embedded default English cue at 0–3 s and saves a French file with its cue at 4–8 s; with French chosen, nothing may show at 1 s, and the French cue must show after a seek to 5 s. Before the repair LibVLC listed `1: Track 1 - [English]` and `2: Track 2 - [French]` and selected 1 (`saved-over-embedded-before-repair-20261024.trx`); after it the case passes 3 / 3 and the 203-case native/caption/download scope passes (`saved-over-embedded-related-20261024.trx`).

## The caption menu's early closing and the original manual report

The October 5 report was that choosing **CC Off** in the menu "immediately returned to English" on the saved Pilot. The October 22 investigation explained a different symptom: the menu closing by itself, or a click on a choice not taking, in about 1 in 30–60 automated runs, when another window takes the foreground. The October 5 investigation already found the same mechanism for its own tool-driven attempts: the desktop tool's clicks dismissed the popup before Off reached the player.

**Decision: the menu-closing explanation does not close the original manual cause.** It covers the automated failures and the tool-driven dismissals. It would match the user's report only if the menu closed before the click, so that English simply stayed selected; nothing records whether that happened, and the report describes Off being chosen and then reverting. No app path that reverts a committed Off was found: Off reloads the input without a caption, and the reload is the only thing that attaches one. In this session all nine real-mouse choices (German and Russian twice; Vietnamese, Swedish, Finnish and Japanese; and re-picking the already chosen Arabic) were logged as the person's input and stayed selected. The only other closes were five Escapes and one deactivation, both sent by my own driver. This session also saw the shared desktop interfere twice (outside Ctrl+Shift+Left presses and a focus change), which supports the outside-activation explanation for automated runs but says nothing about the user's own session. Asked on October 24 whether the menu closed before the click or CC Off showed as selected and then changed back, the user does not remember. The October 5 manual report is therefore recorded as **unexplained**, and no further investigation is planned without a new occurrence. The direct **CC** button, human-confirmed on October 5 and passing again here, is the dependable path for turning captions off and on.

## Arabic and Japanese rendering after 71d20c1

On the 71d20c1 build, at the 1:48 cue, **Arabic (downloaded)** rendered the Commons Arabic line with joined letterforms and right-to-left word order, and **Japanese (downloaded)** rendered "この剣には暗い過去が" correctly. Both choices reloaded the input and kept the paused position within 0.31 s.

**Fault 6: edge punctuation on the wrong side.** The Arabic line "هذا النصل له ماض مظلم." showed its period at the **right** end, beside the first word, instead of the left end where the sentence finishes. LibVLC lays each caption line out with a left-to-right base direction, so neutral characters at the ends of a right-to-left line (periods, question marks, ellipses, leading dialogue dashes) land on the wrong side. A copy of the same file with U+200F (right-to-left mark) at each line end showed the period on the left, in the same paused frame.

**Repair (3659de1).** When a saved provider caption's language is right-to-left (from the culture's own text direction: Arabic, Hebrew, Persian, Urdu and others), the saver writes a right-to-left mark at both ends of each cue text line of SubRip and WebVTT files. Headers, notes, cue numbers, timings and other languages are written unchanged; ASS files are left to libass. `PlaylistMediaDownloadTests.SavedRightToLeftCaptionsKeepTheirPunctuationOnTheirSide` (an Arabic VTT with a note and a two-line cue, a Hebrew SRT with leading dashes and CRLF, and English) failed before the repair (`rtl-marks-before-repair-20261024.trx`); the 149-case download/caption scope passes after it (`rtl-marks-after-repair-20261024.trx`).

This covers captions the app saves from a provider (Watch via download, Download to library, Download season). It does not change right-to-left captions that are streamed, embedded in a container, or shipped beside a torrent's video; those still show edge punctuation on the left-to-right side. No catalog provider in this session offered Arabic captions (Vidsrc's eleven for Sintel were European languages), so the repaired saver's Arabic output was checked on screen with the Commons text written by the same function (`MarkRightToLeftLines`): on the 3659de1 build the period (1:48), the Arabic question mark (2:09) and the ellipsis (3:27) all sat at the left end of their lines, in the same Sintel frames.

## Visible TV journey in the Downloads UI (tv-profile)

The profile's download folder held Breaking Bad Season 1 as saved by the October 9 season QA (seven episodes, each with an English VTT), shared by hardlink the way the earlier QA profiles already share it.

1. **TV Shows → "Breaking Bad" → Search** listed Breaking Bad first among six TVmaze results. Its details offered Season 1 with episodes "1. Pilot" to "7. A No-Rough-Stuff-Type Deal".
2. **Download season** recognized all seven saved episodes without a transfer and reported, 6 s later, "Saved 7 episodes from Season 1, including available captions."
3. **Downloads** listed seven rows, "Breaking Bad · S01E01 · Pilot" to "Breaking Bad · S01E07 · A No-Rough-Stuff-Type Deal", each with Delete and Play.
4. **Play** on S01E03 opened the native player; the menu listed **CC Off, English (downloaded)**. At 5:07.9 the two-line cue "Do you remember it / having an effect on your mood?" showed while paused; it was still there after Downloads → episode tab return; **CC** off removed it (5:08.29) and **CC** on restored it (5:08.70).
5. With the episode tab closed, **Delete → Yes** ("Are you sure you want to permanently delete 'Breaking Bad · S01E03 · …and the Bag's in the River'?") removed the profile's copy; Downloads then listed six episodes. The shared original stayed (its link count returned to 3).

The ten real-app caption variants and four menu diagnostics passed **14 / 14** on 71d20c1 under the lock (`live-caption-selection-71d20c1-20261024.trx`).

**Second tab-order change.** During step 4 the app again lost the foreground, and a click on the Downloads tab left the order "TV Shows, Breaking Bad, Downloads" with Downloads active. That matches a tab drag-and-drop of Downloads onto the episode tab, which the driver never performs. The tab header keeps its drag source after a click, so a stray pressed-button move could start such a drag; whether that or outside input caused it is not established. Noted for the tab-strip owner, not changed here.

## Delete leaves saved captions and metadata behind

Downloads → Delete removes only the video file. For both the Movie and the TV item, `library-item.json` (and, for the episode, `caption-01.en.vtt`) stayed in the item's folder. Downloads hides the item because its video is gone, and a later save creates a new revision folder, so nothing visible breaks; the leftover files only take space. Not changed here.

## Cleanup and preservation

- The 1,172,428,172-byte Sintel download was deleted through the app; the 58 MB Vidsrc copy and its eleven captions were removed by their final player close. The Commons Arabic/Japanese files (3.4 KB) remain as evidence in the ignored QA folder, with half-size screenshots (16 MB in all).
- Each in-app Find sources for Sintel (22:58 and 23:26 on October 23) left a ~55 MB Chromium profile under the profile's `Local/UniversalMediaOS/BrowserProfiles` that survived a normal app close. They were removed after recording; this belongs to the provider/scraper area.
- My hardlinks to the shared originals were removed after the runs.
- All 23 shared originals (Breaking Bad S1 from `catalog-season-20261009`, Sintel from `package-smoke-20261009`) kept their sizes, write times and small-file SHA-256 hashes (`originals-before-20261023.json` / `originals-after-20261024.json`).

## What remains open in this item

- **Spoken Dub and full caption selection for the anime episode-1 dual-audio download.** That sample was a temporary watch removed at its final close; repeating it needs a 3.5 GB torrent, and spoken language needs a listener (or the anime session's speech check). Full caption selection across many embedded tracks is now qualified on Sintel instead.
- **Right-to-left captions that the app does not save**: streamed provider captions, embedded right-to-left tracks, torrent sidecars. They keep left-to-right edge punctuation.
- **Two saved captions in the same language** share one label ("Arabic (downloaded)" twice), because a saved file keeps only its language, not the provider's label.
- **Archive items' own caption files** (for example Cosmos Laundromat's en/es/fr/it SRTs) are never offered; that is the movie-tv-providers session's finding and area.
- **The original manual CC Off report** is recorded as unexplained (the user does not remember which symptom it was); the direct CC button is the dependable path.
