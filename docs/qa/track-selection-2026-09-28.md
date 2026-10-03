# Named native tracks and quality reload checkpoint

**September 28, 2026. Scope:** C / IP07/IP11 named caption/audio choices and quality reload state. Broader anime, presentation, performance and release gates remain open.

## Changes

- Replace generic cycling buttons in Playback options with named subtitle and audio lists. Offer Off explicitly; do not offer VLC Disable as audio. Keep native/site control visibility and automatic provider discovery.
- Select external captions by the actual provider file and its own request context. Attach only the chosen file with native priority 4, retaining one owned caption proxy session. Remember unique label/language identity across episode reorderings; duplicate/missing metadata uses file identity. An unavailable remembered external choice stays Off rather than silently loading another language.
- Restore native audio using declared language and description when unique. Decoder-local numbers and unknown language are not remembered as a verified language. Sources with provider caption files use that list; sources without them use native caption metadata. Mixed embedded/provider caption menus are not complete acceptance.
- Quality/caption reload and native retry preserve current position, manual Off, audio choice, quality options and paused state. Set restoration state before staging the new input; remove the deferred quality continuation. Let the native clock advance at least 250 ms after a reload seek so the caption decoder can render before re-pausing, and use the current clock for queued time updates. Record a pause request even while native buffering delays acknowledgement. A retry following a genuine playback error may restart playback.

The one-file attachment relies on VLC's explicit slave selection, rather than assuming that generic names identify provider files. [VLC priority mapping](https://raw.githubusercontent.com/videolan/vlc/3.0.23/lib/media.c) and [forced slave handling](https://raw.githubusercontent.com/videolan/vlc/3.0.23/src/input/input.c) establish the installed major-version behavior.

## Verification

Final production Debug build: **zero warnings/errors**. **479 selected C# tests passed, zero failures/skips**, including seven added cases covering exact caption attachment/reordered episodes/session release, Off/missing language, ambiguous file identities, declared audio identity, pause intent and short-position restoration setup. Python was unchanged; the preceding checkpoint's 48 Python passes are historical evidence, not a rerun in this batch.

The selected C# suite excludes PlaybackPresentationTests, RetainedPlayerTabsTests, BootWindowTests, DownloadTests, MangaTests, NavigationTests, SearchTests and SettingsTests while the user uses the main monitor. It does not replace complete desktop/release acceptance.

**Native fixture:** final-source native playback passed 110 samples without playback/action errors. The actual decoder showed both 320×180 and 640×360 video. Paused 180p switching near the start, Arabic switching, Japanese audio selection/retention through 360p, Off/quality retention through retry, and 180p switching while playing all passed. The near-start quality hold stayed paused around three seconds after a requested 2.5-second seek; the short advance primes native decoding. The bounded 90-second HLS fixture has 180p and 360p video renditions, English/Japanese-labelled AAC tone renditions and English/Arabic VTT files. Its transport is a local HTTP server and its advertised quality options are supplied by the diagnostic. It exercises the actual player and production selection/reload path; it does not exercise public provider discovery or live quality-manifest discovery. Production HLS group retention and network restrictions remain tested by the suite.

**Real anime:** final production assemblies played the actual stream, paused after an advancing seek, selected Arabic then English with one attached file and resumed. Visible timed English dialogue rendered at 215.272 seconds; a separate final-source Arabic hold visibly rendered Arabic dialogue at 214.752 seconds. Both frame checks used read-only Computer Use on the second monitor. Deleted-track refresh kept the paused real-source audio list to its one actual untagged track. The diagnostic supplies the preceding actual Frieren Sub result to the normal router/proxy/player and uses its actual captured caption files. It bypasses fresh catalog/search/extraction and independent exact-unit matching. It waits for advancing native playback before seeking/pausing; successful named labels alone do not prove that cues rendered. The tested live master offers one variant and one untagged audio track.

Visible checks use isolated data profiles and the second monitor (1280×800 at 2880,478). No agent keyboard, mouse or activation input is used. Diagnostic timers invoke view-model commands and close the app. Read-only Computer Use captures actual windows. For unobstructed caption frame evidence, the diagnostic repeatedly hides its overlay during paused caption holds; that is not a claim that production captions avoid overlapping expanded controls.

## Failures and practical limits

The initial local fixture correctly failed public-network validation; production network restrictions were not relaxed. The final local fixture bypasses that transport. Early native runs reproduced premature pause before duration/seek readiness, stale time updates, captions selected but not rendered when re-paused immediately after a seek, stale audio entries after native stream replacement, and quality choices disappearing after retry. Those observations drove the repairs; failed traces are retained. A diagnostic runtime copy attempted while an earlier test still held its assemblies was rejected; that run is excluded from final-build acceptance. Final diagnostics use a separate output directory with checked production assembly hashes.

The final native English dialogue caption visibly rendered after selecting Arabic then English while paused; the separate Arabic hold also visibly rendered its timed cue. Caption priming advances the native clock slightly; the measured two-switch hold moved from 214.292 to 215.272 seconds (0.980 seconds total). This is current-position retention within native timing granularity, not frame-exact pause preservation.

External caption switching restarts the native input. This preserves file identity without guessing decoder numbering but has startup/buffering cost. Full live-provider/multi-quality, mixed embedded/provider selections, human listening, playing fullscreen/focus/minimize/PiP, DPI/RTL, resource settling, exact episode matching and automatic lookup latency remain open. Expanded controls can cover native captions; caption/control placement needs the remaining B presentation work. Temporary Watch via download, Movie/TV native watch actions, Anna's Archive and Arabic cartoons remain later work.

## Evidence

Final native traces: [quality/audio/retry](C:/Users/user/animeapp/.artifacts/implementation/track-selection-20260928/native-quality-final-profile/playback.jsonl), [English after Arabic/English switches](C:/Users/user/animeapp/.artifacts/implementation/track-selection-20260928/native-caption-prime-profile/playback.jsonl), [visible Arabic hold](C:/Users/user/animeapp/.artifacts/implementation/track-selection-20260928/native-arabic-frame-profile/playback.jsonl). Read-only frame captures are displayed in this chat; screenshot payloads were not saved or decoded. The diagnostic service bar remains pending because dependency preparation is disabled in its isolated profile; this is not service-health acceptance.

- [Final selected TRX](C:/Users/user/animeapp/.artifacts/implementation/track-selection-20260928/results/track-selection-verified.trx)
- [Production build log](C:/Users/user/animeapp/.artifacts/implementation/track-selection-20260928/build-final.log)
- [Starting source snapshot](C:/Users/user/animeapp/.artifacts/implementation/track-selection-20260928/before-manifest.json)
- [Source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/track-selection-checkpoint.json)
