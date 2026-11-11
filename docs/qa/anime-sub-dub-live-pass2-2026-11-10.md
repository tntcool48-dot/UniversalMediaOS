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

## Visible real-app journey with live captions (November 11, 02:58–03:15)

A temporary FlaUI journey, not committed (`probe/LiveAnimeSubCaptionJourneyQa.cs.txt`), drove the real app built from the merged code. It ran under the desktop lock, in AppFixture's isolated profile, on the second monitor, muted. The fixture's AniList mock forwarded every catalog request to the real keyless AniList with a generic client name; scraping was live and nothing was pinned. Each step checked that the app's own window was in the foreground before input. Only the app window was captured.

Journey: search → exact catalog card → episode → **Sub** → ⚡ Watch Now → Stream. Saved rows were seeded at 600 s for the unit, 400 s for the next and 300 s for the previous.

| Run | Unit | Outcome |
|---|---|---|
| 1 | Spy x Family ep 3 Sub | No player within 150 s; the details outcome was not captured (harness fixed) |
| 2 | Chainsaw Man ep 2 Sub | Honest **"No usable stream was found…"**: Anikoto used 26 s with no usable server; Miruro and Re:Anime ran out of budget |
| 3 | Chainsaw Man ep 2 Sub | Unavailable once, then a second **Watch Now** played. English default and the unverified-Sub notice visible; Arabic chosen while paused. **Next → ep 3 honestly unavailable** ("No playable source was found for episode 3."), and ep 2's frame and position were kept |
| 4 | Chainsaw Man ep 2 Sub | Played. Switching English → Arabic → English while paused left the player at **0 s and playing** at the check (see below) |
| 5 | Chainsaw Man ep 2 Sub | **Full path passed** (details below); the Dub switch was unavailable |

Run 5, all with the app visible:

- Opened at its saved position (**600.6 s**) with the notice **"Sub server selected · audio language unverified"**, a single "Track 2" audio choice, and **English** provider captions. At paused 10:00 an English cue is rendered on the video (`journey-run5-captures/01`).
- English → Arabic → English → Arabic while paused: the log shows each reload restored its paused clock (600.6 → 601.2 → 602.1 s) and paused again.
- **Next → episode 3**, re-resolved through rotation (Anikoto again), opened at its own saved **400.7 s** with **Arabic kept**, the same notice, "Episode 3 of 12", and a **two-line Arabic cue rendered** at paused 6:40 (`…/04`).
- **Previous → episode 2** loaded its saved 603.0 s (see the open fault below).
- **Sub → Dub switch:** Details → Dub → Watch Now → Stream ended honestly with **"No usable dub stream was found. Try another episode, choose Sub, or explicitly select Open website."** No Dub tab opened, so caption language and position across a Sub/Dub switch could not be shown live.

Result: 1 / 1 passed in 3 min 15 s (`journey/journey-031539.trx`, SHA-256 `009cc99c…`), with the full sanitized app log beside it.

### Findings

- **Paused before the stream reports its length (open fault).** In run 5 the journey pressed Pause about a second after episode 2 started again on Previous. That was after the Playing event but before the live HLS stream reported its length, which arrived 1 s after Playing on every load here. No length change and no `[Resume] Restored` line followed, so the player stayed **paused at 0:00 instead of its saved 603.0 s**. The saved row was not overwritten, because positions under 5 s are never saved. Whether Play then restores 603 s was not checked. Run 4's "0 s and playing" after a caption change while paused was probably the same timing: the reloads in run 5's log restored correctly once the length arrived. Run 4's log was only an excerpt, so this was not proven. The local test streams report their length before Playing, so no deterministic failing check exists yet. Following the rule of a failing check before any repair, **this is recorded as open, not changed.** A likely repair is to keep a pause request made before a pending resume lands until the restore applies, as a paused reload already does.
- **Mid-reload captures.** Runs 4–5 captured while a reload was still in progress: the old "Play" label satisfied the wait before the reload began. Those snapshot positions (594 s, "playing") are harness artefacts, not app faults. The app log is the authority for those steps.
- **Caption memory across a Sub/Dub switch is per tab.** A Sub/Dub switch is a new Stream request from Details, and so a new player tab. That tab starts from the unit's shared saved row, but not from the previous tab's caption choice. A remembered caption language across tabs would be a persisted setting, which is a product decision; it was **left unchanged** and recorded as open.

## Still open after this pass

- The user's listening check of the three first-pass WAVs (unchanged, in `anime-sub-dub-20261023/live-private/`).
- A second provider serving media: none did, even with Anikoto hidden. A live provider switch and a live Sub↔Dub switch (Dubs were unavailable for Chainsaw Man, Demon Slayer and Spy x Family ep 3) remain unqualified.
- The pause-before-length fault above, and the per-tab caption memory decision.
- Full-duration spoken audio. Captions: Demon Slayer English spans the whole episode; Chainsaw Man stops 98 s early.
