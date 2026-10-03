# Direct-stream fallback investigation — September 28, 2026

## Confirmed cause

The previous live automatic Sub lookup found Miruro's HLS stream but opened its site. The scraper logged an HTTP 200 segment with `Content-Type: image/jpeg`, rejected that response and returned `requires_webview: true`. `TripleNetHandoff` immediately accepted that saved page. Its message saying native candidates were exhausted did not describe the actual route: the first captured stream failed validation and short-circuited the remaining extraction stages.

A bounded replay of that response confirmed MPEG-TS video. Consecutive 188-byte packets had headers `47401110`, `47400010`, `47500010` and `47410030`; this was not a JPEG image. Both Python validation and the C# HLS proxy rejected all `image/*` responses before checking the bytes. Correcting only the scraper would therefore have left native playback blocked at the proxy.

## Focused correction

- Check four consecutive valid MPEG-TS packet headers within the first packet width, including responses starting between packets. Accept an image MIME label only when this video signature is established.
- Keep real JPEG/PNG/GIF signatures, HTML/JSON responses and malformed payloads rejected.
- In the proxy, inspect a bounded 1,024-byte prefix, send verified transport streams as `video/MP2T`, and replay every inspected byte before copying the remaining response. Preserve upstream status, length, range and seek headers.
- Replace the misleading routing message with the actual reason for opening the captured page.

The normal index discovery and browser fallback remain. No provider domain rule, new key, setting or data migration was added.

## Before/after live extractor probe

The probe imports the production scraper and invokes its existing extraction flow on the same previously selected page, using isolated browser profiles. Request context and signed media URLs are retained only in local ignored artifacts; the summary below excludes them.

| Evidence | Before correction | After correction |
| --- | --- | --- |
| Playlist response | HTTP 200, valid `#EXTM3U` | HTTP 200, valid `#EXTM3U` |
| First segment | HTTP 200, `image/jpeg`, MPEG-TS bytes | HTTP 200, `image/jpeg`, MPEG-TS bytes |
| Resolver result | `requires_webview: true` | `requires_webview: false` |

Local evidence: `.artifacts/implementation/stream-fallback-investigation/segment-evidence.json`, `after-summary.json`, and before/after captures. The latter contain request context and are not suitable for publishing.

## Regression verification

Build `.artifacts/implementation/direct-ts-build`: zero warnings/errors. The focused C# selection passed **36 cases**; the full exact-build suite passed **457**, with **22 existing skips** and **zero failures**. Python discovery/matching/payload tests passed **23 cases**, including seven new payload/routing cases. Seven added C# cases cover MPEG-TS recognition, unaligned ranges and image/corrupt-payload rejection. These tests do not independently establish live native playback.

Full results: [TRX](C:/Users/user/animeapp/.artifacts/implementation/results/direct-stream.trx). The built scraper's SHA256 matches the source: `0B431EC9CE63871DABA30FEEA8ACEA3A6592191B606A9DDE5E6D647A80C609D2`.

## Native app verification

Exact build, process 53784, using the existing isolated `.artifacts/implementation/iframe-resume-manual-20260917` profile with real services. Requested Mushoku Tensei: Jobless Reincarnation Season 3, AniList 178789 / MAL 59193, episode 1, Sub, through Stream (Auto).

- Required Python, HLS, FFmpeg and uBlock service checks passed.
- Automatic resolution ran **01:55:09–01:55:52**, approximately **43 seconds**, and reached Miruro after AniKoto. The production log reported verified MPEG-TS despite `image/jpeg`, then registered a **Tier 1 proxied HLS stream**. The captured page was not opened in WebView.
- LibVLC restored the existing **535.8 seconds** at 01:55:53, reported a 23:39 duration and played visible video. The native app showed an advancing 09:26 timeline; a single app Pause at 01:56:46 stopped at **09:43**.
- App +30s changed the position to **10:13**. Play then displayed the later scene and continued through **12:03** in the same native player. A keyboard Pause stopped there; the app remains open and paused for inspection. This verifies that the prefix inspection/replay did not prevent actual stream decoding or this seek.
- The player reported a **Japanese** audio track. This is track metadata, not an independent listening assessment. **CC was disabled**, with no native subtitle track available. The previous browser flow rendered English subtitles; direct extraction currently carries the stream/request context without a separate caption handoff. Fix and verify that handoff before accepting complete native Sub playback.

An upstream connection-closure warning appeared after the initial restored seek; visible playback and position updates continued. No browser-fallback or player-error event followed it. This run does not close transfer-lifetime acceptance.

The launch record and frozen log are under `.artifacts/implementation/direct-stream-live`. Media URL/request context remains local; the frozen log omits upstream URLs. The source/build hashes are in [the checkpoint manifest](C:/Users/user/animeapp/.artifacts/implementation/direct-stream-checkpoint.json).

## Remaining limits

This correction addresses the demonstrated false rejection of MPEG-TS mislabeled as images. It does not establish native support for genuinely browser-bound/encrypted responses, independently audible language, all providers, changed-provider resume or faster provider search. Exact season/part matching, source lookup latency and whole-app/release acceptance remain on the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md).
