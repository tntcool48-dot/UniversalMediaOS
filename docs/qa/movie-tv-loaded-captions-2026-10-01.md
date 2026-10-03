# Movie/TV loaded captions — October 1, 2026

The normal production source/provider/native-player path now renders English captions for Breaking Bad S1E1 and S1E2. Both episodes independently verified their returned IMDb/season/episode metadata, decoded, sought into a timed cue, paused inactive, stayed paused on return and resumed on command. These are offscreen native checks, not acceptance of the real catalog-button/fullscreen journey.

## Cause and repair

The diagnostic production run established a specific failure: `player_ready=True`, one caption candidate and almost three seconds of caption budget remained, but our caption-validation request returned **HTTP 429**. The provider browser already held parsed, timed cues for the bound release. Our second client request discarded those usable captions. This was not a timeout.

The scraper now reads those already loaded cues from the active player snapshot and converts them to bounded WebVTT. It uses them only when the decoded item response, current release filename and extracted stream agree. Missing or conflicting item evidence cannot attach a different release's in-memory cues. Invalid/nonfinite/reversed or sub-millisecond empty times, empty text, excessive cue counts and content over two MiB are rejected; content is escaped for timed-text output. Remote-only caption files retain the existing timed-payload validation.

`MediaSubtitleTrack` carries optional in-memory WebVTT through the existing source/message/player copies. The caption proxy serves that content locally without another remote caption GET; it uses a `.vtt` route even when the original URL ends in `.srt`. The selected caption retains its language/name and file identity. Retry keeps the payload; replacement and player release remove the owned proxy session. Movie/TV source serialization continues to omit captions, so this payload does not become persisted library data. The existing unknown-audio notice remains truthful; an English caption does not prove spoken audio.

A second repair removes duplicate outer-provider retries. C# already queues the other candidates, while Python previously retried those same providers inside every `resolve` operation. Coordinated resolution now retains the candidate's own mirrors and leaves other candidates to the existing C# queue. Standalone `extract` still supports its full fallback list. This reduces redundant requests without adding another provider system or discarding the queued alternatives. Removing duplication does not by itself prove the cause of every upstream 429. Neither the three-second caption budget nor the overall 34-second extraction budget was increased.

Before that second repair, one intermediate native run still exhausted caption readiness with no loaded cues. These repairs address duplicate fetching/work, not all possible upstream caption outages.

## Verification

- **79 focused C# cases passed**, with five new cases covering native in-memory caption handoff without origin retrieval, retry/owned-session release, rejection of empty/HTML/script decoys, the UTF-8 byte limit and omission from persisted source JSON. Existing stream-JSON coverage now checks the loaded WebVTT field.
- **147 Python cases passed**, with four new cases covering reuse without a second HTTP request, wrong-release rejection, timing/text/size limits and coordinated mirror versus standalone alternative coverage.
- Full C# regression: **570 passed, 22 existing skips, 592 total, zero failures**, in 1 minute 35 seconds. Results: `UniversalMediaOS.Tests.E2E/TestResults/movie-tv-loaded-captions-2026-10-01.trx`. The preceding rapid-search UI failure passed in this run without any search-code repair; its original cause has not been established.
- The WPF and native helper builds completed with zero warnings/errors. Final source/output script SHA-256: `FC00AB22F20C54F4DFA258E441AED05965B69C52FB2C6B1B734FA528DD05DBCF`; WPF DLL: `A5D1D002D7F35103680FCCC36E3C00AF600AAE0DBB8B6336594325B039476415`.

The final TV helper used the production Python bootstrapper, source coordinator, exact matcher, proxy and `PlaybackViewModel` under a WPF dispatcher. It ran sequentially in one isolated profile, muted playback, decoded into memory and opened no window. It selected a real cue from the loaded content for each captured frame rather than fetching a caption again for the test. Native English glyphs were visually confirmed in both images.

| Exact requested unit | Lookup | Loaded caption UTF-8 bytes | Native frames | Inactive paused position | Position after resume |
| --- | ---: | ---: | ---: | ---: | ---: |
| Breaking Bad S1E1, `tt0903747` | 20.801 s | 40,779 | 85 | 137.656 s | 138.260 s |
| Breaking Bad S1E2, `tt0903747` | 21.765 s | 31,518 | 88 | 134.581 s | 135.182 s |

Both caption selections were named English and VLC selected native subtitle ID 2. Both had one native audio track, no independent audio-language tag and the unverified-audio notice. Each returned separately verified episode metadata and used the sampled HLS startup rendition. Frame counts are checkpoint samples, not throughput measurements; these lookup times are not controlled latency comparisons. The helper exited zero. Its existing memory-output teardown and asynchronous resume-save diagnostics are not claimed as repaired or as restart-persistence acceptance.

Sanitized results: `.artifacts/implementation/movie-tv-evidence-20261001/loaded-native-results.json`, with individual `tt0903747-S1E1-loaded-native-result.json` and `tt0903747-S1E2-loaded-native-result.json`. Images: `tt0903747-S1E1-loaded-caption.png` and `tt0903747-S1E2-loaded-caption.png` in the same directory. Python results: `python-loaded-captions.log`. Raw local captures and entire caption text are not embedded in this document.

## Remaining limits

A combined movie/TV attempt stopped at Dune because a fast byte-checked stream had no independent item identity. The coordinator is allowed to expose that as an unverified Try stream result, but it can cancel a slower verified alternative. This observation does not establish a wrong film, and it does not pass movie identity acceptance. Source preference/continued verification needs the next focused Movie/TV repair. Dune's previous native English-caption checkpoint remains dated evidence; it was not repeated successfully in the final combined run.

Finish the actual secondary-monitor catalog-to-player journey, broader provider outages, full subtitle synchronization/track selection, correct movie remakes/cuts, actual audio, a second TV season/special mapping, and close/restart/provider-change resume. The desktop testing runtime failed before execution in the preceding batch; no manual desktop testing was performed here. HLS/DASH downloading, DASH segment validation and TV season transfers also remain open. The seven feature areas in the main checklist are unchanged; this completes a specific caption failure repair and two adjacent native episode samples.
