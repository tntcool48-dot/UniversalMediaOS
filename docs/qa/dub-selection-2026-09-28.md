# Usable Dub selection and native caption controls — September 28, 2026

The previously rejected Frieren Dub stream contains usable English speech. The strict metadata gate added in the preceding caption batch blocked it because its audio has no language tag. This batch fixes that rejection without adding an English tag or treating the provider's label as verified spoken language.

## Implementation

The scraper records the selected provider audio only when clicking the requested server produces one changed player iframe. That selection follows the chosen iframe and its nested player into the media result. An unchanged iframe, multiple changed frames, unrelated player or merely requesting Dub provides no such evidence. Explicit Sub/Dub groups take precedence over overlapping words such as “English subtitles.” Selection is reset for a new outer page or server rotation.

Validated media from that selected Dub frame can play when audio metadata is absent/undefined. Known non-English metadata still rejects the result, even when the provider calls it Dub. `audio_languages` remains empty/undefined; `selected_audio` is a separate protocol field. The C# handoff applies the same rule.

Native Playback options show **Dub server selected · audio language unverified** for this case. The notice survives initial handoff, retry, quality reload and adjacent-episode results. Failure messages now describe missing playable Dub without implying a language tag is mandatory. The audio button cycles actual tracks only; VLC's Disable entry is excluded and a single-track source disables switching.

No new product dependency, API key, provider picker, static provider domain or data migration was added. The temporary diagnostic uses the already installed Whisper package and cached base weights; speech recognition is not part of the app.

## Verification

The final solution build has zero warnings/errors. **472 selected C# tests passed, zero failures/skips**, including four added provider-selection cases. The caption retry test also verifies notice retention. **48 Python cases passed**, including six added selection regressions. The same eight desktop-input classes excluded in the preceding batch remain excluded; these counts do not establish the full release suite or resolve historical skips.

Fresh direct episode checks returned native Dub in 20.41 seconds initially and 19.89 seconds with the final group/frame checks. Episode 2 returned native Dub in 19.70 seconds. Sub returned native media with nine captions in 20.39 seconds. These known episode routes do not benchmark automatic catalog/index discovery.

A bounded sample from approximately 200–230 seconds of the final episode-1 Dub source decoded to audio. Local Whisper automatically detected English and transcribed coherent dialogue naming the party and thanking them for defeating the Demon King. Its sample and source JSON hashes are retained. The earlier 95-second music sample produced low-confidence fragments and is not language evidence. This is automated speech evidence for that sample, not a human listening check or verification of every episode/source. The app continues to display the truthful provider-label notice.

An isolated native diagnostic fed the actual captured results through the **production router, HLS/caption proxy, MainViewModel and player**. It drove view-model commands within the test process, without OS keyboard/mouse input. It bypassed catalog/search UI and reused captured episode results for navigation, so it does not prove fresh adjacent-episode discovery or independent exact-unit matching.

| Check | Result |
| --- | --- |
| Native Dub | 200 timer samples; no playback error. Seek to dialogue, pause/resume, forward seek and retry worked. |
| Caption state | Off stayed off across seek/retry; enabling captions afterward restored a native track. |
| Adjacent Dub episodes | The separately extracted episode-2 source played; returning to episode 1 resumed its saved position. Caption attachment and the language notice survived. |
| Native Sub | Nine tracks loaded. A read-only screenshot showed an English dialogue cue at approximately 03:31 after seek. Off/seek/retry/on checks advanced without playback errors. |
| Single audio | Switching is disabled during advancing playback with one real audio track. Initial/source-transition samples at zero time briefly exposed stale availability; those do not establish settled track state. The Sub diagnostic also invoked the command and retained its active audio. |
| Quality | Both actual masters advertised one variant and no alternate audio renditions. The diagnostic logged “no alternative advertised quality”; quality switching and multi-audio selection remain unverified. |
| Monitor | Successful windows logged `Left=2880, Top=478, Width=1280, Height=800` on the second monitor, used `ShowActivated=false`, muted playback and automatic shutdown. No agent OS input/activation was used. |

The diagnostic service health bar is not dependency-health evidence: automatic service preparation was disabled in these isolated profiles. A first attempt to compose the diagnostic source failed before launching; the corrected diagnostic compiled with zero warnings/errors. Production builds remained successful.

## Artifacts and remaining work

[Final app](C:/Users/user/animeapp/.artifacts/implementation/dub-selection-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [final selected TRX](C:/Users/user/animeapp/.artifacts/implementation/dub-selection-20260928/results/dub-selection-background-final.trx), [Python log](C:/Users/user/animeapp/.artifacts/implementation/dub-selection-20260928/python-tests-final.log), and [source/build/evidence manifest](C:/Users/user/animeapp/.artifacts/implementation/dub-selection-checkpoint.json). Captured URLs, request context and bounded media samples stay in ignored private artifacts.

Finish named language selection/reordering across multiple captions/audio tracks, real multi-variant quality changes and the broader B/D player/discovery acceptance. External WebVTT currently exposes generic native track names. The installed VLC WebVTT demuxer does not populate language/description in its format, while its text-subtitle demuxer detects language from a filename; a UI language mapping must not guess asynchronous track order. References: [VLC 3.0.23 WebVTT demuxer](https://raw.githubusercontent.com/videolan/vlc/3.0.23/modules/demux/webvtt.c), [VLC 3.0.23 text-subtitle demuxer](https://raw.githubusercontent.com/videolan/vlc/3.0.23/modules/demux/subtitle.c).

Movies/TV, temporary-watch downloads, books and Arabic cartoons remain in the [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md). This closes the reproduced untagged-Dub rejection and a single-audio control defect, not the whole anime gate.
