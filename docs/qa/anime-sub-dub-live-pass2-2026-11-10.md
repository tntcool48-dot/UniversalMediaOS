# Sub/Dub second pass on the merged code — November 10–11, 2026

Branch `claude/anime-sub-dub-audio-4da49a` at the merged wave-1 code (`401155b`). First-pass repairs and evidence: [first pass](anime-sub-dub-retention-and-providers-2026-10-23.md).

## Merged code still passes

The rebuilt merge passes my focused and related checks: **101 / 101, zero failures or skips, in 50 s**. That covers the three retention regressions, the four unknown-Sub-audio routing cases, and SubtitleHandoffTests, NativeAudioPreferenceMatchingTests, LocalEmbeddedCaptionPreparationTests and AutomaticResolutionRegressionTests. It also covers the real-decode buffering Retry, embedded-caption Off/on and anime audio-preference cases. The merged `AttachExternalSubtitles` (auto-generated captions never chosen by default; otherwise `Remembered(...)`) and `KnownLanguage` via `CaptionLanguage.IsKnown` keep the caption-language regressions green. Evidence: `merged-regressions-20261110.trx` in the worktree's ignored `.artifacts/implementation/anime-sub-dub-20261110`.

## Live sampling (November 11, 02:49–02:58)

The same bounded probe as the first pass, on the merged `scraper.py`, held the desktop lock for its off-screen Chromium. It adds caption coverage measures: cue span over the media duration, the tail after the last cue, the largest gap between cues, and gaps over 60 s. As before, the app's own arguments were used, with automatic index rotation (no pinned provider), the 63-second budget and an owned data root. One 30-second sample from about 10:00 per audio stream went through the existing cached Whisper base model on the CPU, with no transcript kept.

| Request | Outcome | Whisper | Provider captions |
|---|---|---|---|
| Demon Slayer (101922) ep 2 **Sub** | Native, Anikoto, 19.7 s; title + MAL + episode 2 consistent | **Japanese 0.990**, 5 speech segments (one untagged AAC track) | 3 label-only files (English, Portuguese (Brazil), Spanish (LA)). English: 262 cues, 72 multiline, **6.4–1420.0 s of 1420.1 s** (span 0.995); largest gap 95.5 s, 2 gaps > 60 s |
| Demon Slayer ep 2 **Dub** | Unavailable after 3 sites, 66.5 s | — | — |
| Chainsaw Man (127230) ep 2 **Sub** | Native, Anikoto, 17.2 s; consistent | **Japanese 0.983**, 8 speech segments | 9 label-only files. English 307 cues (92 multiline), Arabic 308 (41): **11.2–1336.9 s of 1435.1 s**, so the last **98 s have no cues**; one 94.6 s gap |
| Chainsaw Man ep 2 **Dub** | Unavailable, 65.9 s | — | — |
| Bocchi the Rock! (130003) ep 2 **Sub** | Unavailable, 67.0 s | — | — |

The provider declares no audio language. No language codes appear on any caption file either: they carry labels only, and some contain mis-encoded characters (`Portuguese (- Portugu�s (Brasil))`). Full-duration coverage differs by title. Demon Slayer's English file spans the whole episode. Chainsaw Man's stops 98 s before the end, likely the ending song and preview. This was not checked by eye. One-minute gaps match where songs usually play, but no single file guarantees dialogue coverage.

### Second provider

To see whether any provider other than Anikoto serves media, a second run hid Anikoto from the probe's index view only. Production rotation is unchanged. Spy x Family episode 3 Sub and Dub and Frieren episode 3 Sub and Dub were **all unavailable**, each after 3 sites in about 65.5 s:

- **Miruro** (the index now lists `miruro.cx`): no candidate links, or a consistent episode without a catalog ID before its budget ended.
- **Re:Anime**: unknown identity.
- **AniSnatch** (newly indexed): no candidate links.

No second provider served media, so a live switch between providers, with position and caption language kept, **cannot be qualified this pass**. It stays covered only by the controlled regression from the first pass.

Receipts and sanitized logs: `live-A/`, `live-B/`. The three first-pass WAV samples are untouched in `.artifacts/implementation/anime-sub-dub-20261023/live-private/`, still waiting for the user's listening check.
