# Movie/TV native stream extraction — October 1, 2026

Automatic Movie/TV extraction now inspects the actual loaded player frames and returns media that passed a byte check. The final production-service/native-player probe decoded video for the requested Dune (2021) feature and Breaking Bad S1E1, sought to one minute, paused on tab deactivation, retained pause on return, and resumed through the player command. These samples establish native playback through the repaired path; they do not independently establish the film cut, episode identity, spoken language or subtitles.

## Failure and repair

Before this batch, the same IMDb requests returned only website fallbacks after **35.406 seconds** for Dune and **35.422 seconds** for Breaking Bad. A working player was hidden several frames deep. The crawler treated scripts and trackers as child pages, queued other provider roots before real player children, and navigated nested signed frame URLs as top-level pages. This discarded their parent/referrer context; observed player URLs that worked inside the loaded iframe returned 404 when opened separately.

The repair keeps the loaded frame hierarchy intact, tries actual player controls, and prioritizes player children over unrelated roots. Static extraction uses at most five seconds of the existing 34-second Python extraction budget, reserving the remaining time for the interactive player. Script, tracker and private-address pages do not enter the static player queue; small hidden browser frames are skipped. Known movie/TV kind, IMDb, season and episode conflicts are rejected, including redirects and traversal through wrappers without their own identity. Unknown identity remains unknown.

HLS candidates must contain a valid playlist and an accessible media segment; direct media must pass the existing payload check. Failed candidates leave native extraction running. The deadline now reaches those validation requests without changing the default anime validator calls. An MPD is XML, so DASH manifest-only candidates cannot pass the generic payload-size check as byte-checked native media; this scraper still needs DASH segment validation.

The sampled HLS rendition travels through the source model, playback message and native proxy selector. The player starts that rendition while retaining the master audio/subtitle groups, and keeps the hint on retry but clears it for another source. The scraper no longer invents an English language tag from the requested preference. Missing tags and missing independent source evidence keep their unverified notice in playback.

The C# provider returns its first byte-checked native success and cancels the remaining active crawlers instead of waiting for every alternative. It runs at most two scraper browsers at once and keeps later candidates queued rather than dropping them. Earlier four-browser integration runs sometimes returned only website candidates; there was no diagnostic proving whether load timing, provider behavior or contention caused each miss. The bounded final run passed, but this is not a provider-reliability guarantee. Byte-checked sources appear progressively as **Try stream / match unverified** while other providers continue. Open website remains a separately chosen fallback.

## Verification

- **57 focused C# cases passed.** Eight added C# cases cover checked-native cancellation, alternative failure, browser concurrency and later-candidate coverage, unchecked media rejection, explicit website results, JSON rendition/audio handoff, proxy retry/source changes, and progressive unverified display.
- **133 Python cases passed**, including 11 new Movie/TV cases for nested loaded frames, unsafe/tracker exclusion, child priority, wrong IDs/units, unknown-wrapper identity retention, conflicting redirects, media/deadline validation and DASH XML rejection. Existing anime cases pass with their original validator behavior.
- Full C# regression: **560 passed, 22 existing skips, 582 total**, zero failures, in 1 minute 37 seconds. Results: `UniversalMediaOS.Tests.E2E/TestResults/movie-tv-native-frames-2026-10-01.trx`. The WPF and native probe builds completed without warnings or errors.
- The final probe used the production Python bootstrapper, audiovisual source provider, HLS proxy and `PlaybackViewModel` under a real WPF dispatcher. It suppressed app startup, decoded into memory, muted playback and opened no window. The primary monitor was not used.

| Requested item | Production lookup | Decoded video frames | Inactive pause | Position after commanded resume | Native audio tracks |
| --- | ---: | ---: | ---: | ---: | ---: |
| Dune (2021), `tt1160419`, feature | 17.890 s | 92 | 60.782 s | 61.435 s | 1 |
| Breaking Bad, `tt0903747`, S1E1 | 17.378 s | 49 | 60.000 s | 60.271 s | 1 |

Both results were native HLS, had `media_validated=true`, carried a validated startup rendition, had no audio-language tags and had no independent source evidence. Frame counts are checkpoint samples, not throughput measurements. Native audio track discovery does not establish the spoken language. No subtitle rendering or human listening was performed. Network samples are not a controlled latency or accuracy guarantee.

The successful helper exited with code zero. An earlier helper freed its memory-output buffer before disposing the player and failed during cleanup after decoding the movie; the helper's disposal order was corrected. VLC logged converter/on-top diagnostics around memory-output teardown, including in the successful run. No production player teardown change was made for these helper diagnostics.

The sanitized result is `.artifacts/implementation/movie-tv-native-20261001/native-results.json`; helper sources and Python results are alongside it. Raw local diagnostic captures include expiring provider URLs and are not embedded in this document. The source and deployed audiovisual script matched SHA-256 `2230CDC0209DA5BCEC000CC9FAB1D169F65BE832BFB2FEB666629D27AC2B5514`. The tested WPF DLL hash was `C0160949415D32AA992766B1463C3E29A6A1E0E5553B0B4190C092A6C7650492`.

## Still open

Produce independent provider-observed film/episode and audio/subtitle evidence; verify remakes/cuts and TV adjacent episodes, different seasons and specials. Run the actual catalog-button-to-player journey on the secondary monitor, including fullscreen/tab return and close/restart/provider-change resume. Complete season-wide TV downloads and the remaining download acceptance. DASH segment validation and broader provider-outage/cancellation latency remain open. Wikidata/TVmaze remain the keyless defaults; this batch added no metadata key or provider picker. Anime acceptance, Books and Arabic cartoons are unchanged by this checkpoint.
