# Sub/Dub position and caption-language retention, live providers — October 23, 2026

## Reproduced faults and repairs (2b2a9f6)

**Position through Retry and early track changes.** A native reload with no decoded clock yet took its position from the zeroed clock. Retry after the selected provider host failed the first request reloaded the unit at **0 seconds**. Because a reload marks its position as explicit, the unit's saved row was never applied. A sidecar caption choice made while the episode was still opening did the same thing, and it also **paused** the reload, though the person never paused. Later progress saves would then have overwritten the saved position with the restarted one.

The reload now keeps the input's resume target when it has not been applied yet. When there is no decoded clock and no pending target, it reads the unit's saved row. A decoded clock still wins, an explicit restart after completion still starts at zero, and the post-start buffering Retry keeps passing its captured clock and pause intent. An input that is still opening (busy, no error, no Playing event yet) keeps its original start intent instead of being treated as paused.

**Requested caption language through provider changes.** Caption choices were remembered only by an exact key of declared language plus label. When the next provider (Next/Previous, navigation Retry) declared the same language under another label (`English`/`en` → `English [CC]`/`eng`, `Arabic`/`ar` → `العربية`/`ara`), no file was attached and the player showed **CC Off**. Turning captions off and on again on a later provider did nothing, because the remembered key was missing.

Caption choices now also remember their **declared** language. When the exact key is missing, the player falls back to the single choice that declares that language, the same rule audio tracks already used. This applies to provider files, decoded embedded tracks and the Off/on toggle. Two files of the requested language, or a file whose language is only implied by its label, stay unresolved (CC Off) rather than guessed. An explicit Off stays Off. Labels and declared languages are provider metadata; they do not establish the spoken audio.

**Checks.** Three new regressions failed before the production change (**0 passed / 3 failed**):

- Real loopback HTTP: the provider returns 404 for the first request and serves the file on Retry. The decoder reached only **11.9 s** after 12 s of playback from zero, short of the saved 17 s.
- A real local MKV with a sidecar `.en.srt`: CC Off chosen while opening left the unit **paused at 0 s**.
- Provider caption sequence: the second provider's Arabic file showed **CC Off**.

After the repair, the three new regressions and the affected and related scopes pass **49 / 49, zero failures or skips, in 39 s**. Those scopes are SubtitleHandoffTests, NativeAudioPreferenceMatchingTests and LocalEmbeddedCaptionPreparationTests, plus the real-decode buffering Retry, embedded-caption Off/on, anime audio preference and manual audio-language cases. The three new cases then passed three more consecutive runs (**9 / 9**). Each real-decode case checks the saved row afterwards: the retried unit keeps its own position (16.5 s or later), and the adjacent episode's 11 s row stays unchanged. No UI (FlaUI) test, broad suite or physical app journey ran for this repair. Evidence: the before/after TRX and logs in `.artifacts/implementation/anime-sub-dub-20261023` (worktree, ignored), with a copy of the TRX under the main checkout's `.artifacts/implementation/anime-sub-dub-20261023`.

## Unknown Sub audio without a reported server (80476ef)

The native handoff picked its audio notice only from the server the provider reported selecting. The production resolver leaves that choice unset when the episode page itself carries the media, has no Sub/Dub controls, or changes several player frames at once. A **Sub** request in that state, with no declared Japanese track, opened with **no notice at all**, which reads as confirmed audio. Dub was already safe: it is accepted only with declared English audio or a reported Dub server.

Without a reported choice, the handoff now needs a declared language for the requested audio; otherwise it shows the existing localized **"Audio language unverified."** A reported server keeps its existing wording, and declared Japanese (Sub) or English (Dub) still clears the notice. Four new routing cases cover a missing, undetermined, English-only and Japanese declaration. Before the repair **3 failed / 1 passed**, the declared-Japanese case being correct already. After it, the whole affected routing class passes **42 / 42 in 255 ms**. Evidence: `notice-before/after-20261023.trx` beside the files above.

## Live providers, metadata, captions and automated speech (first run)

A bounded probe ran the production `scraper.py` resolver (scraper SHA-256 `094a2759…`), with the app's own arguments: English query, English/Romaji aliases, AniList/MAL IDs, up to eight synonyms, six sites and a 63-second budget. It used an owned data root and held the shared desktop lock for its off-screen Chromium. Providers came from the automatic index rotation; nothing was pinned. Each native result's smallest HLS variant was read with the captured replay headers. One 30-second sample from about 10:00 into the episode went through the existing cached Whisper base model on the CPU, with no language hint and no transcript kept. Provider caption files were read for timing and multiline-cue counts only.

| Request | Outcome | Identity evidence | Declared audio | Whisper (30 s from ~10:00) | Provider captions |
|---|---|---|---|---|---|
| Frieren (154587) ep 5 **Dub** | Native, Anikoto, 19.6 s, 1 site | title + MAL 52991 + episode 5 consistent; Dub server selected | none | **English 0.952**, 6 speech segments | 1 file, label "English", no language code; 55 cues 153.6–1469.9 s of 1470 s, 3 multiline: partial/signs captions, not full dialogue |
| Frieren ep 5 **Sub** | Unavailable after 3 sites, 66.3 s | Anikoto: exact unit + MAL, but its selected host reported the file unavailable; Miruro: unit without catalog ID; Re:Anime: unknown | — | — | — |
| Spy x Family (140960) ep 2 **Dub** | Native, Anikoto, 18.8 s | title + MAL 50265 + episode 2; Dub server | none | **English 0.906**, but only 1 speech segment | 1 file "English", no code; 537 cues 104.6–1458.5 s of 1465 s, **372 multiline** |
| Spy x Family ep 2 **Sub** | Native, Anikoto, 19.1 s | title + MAL + episode 2; Sub server | none | **Japanese 0.990**, 17 speech segments | **12 files** (Arabic, English, French, German, Indonesian, Italian, Portuguese (Brazil), Russian, Spanish ×2, Thai, Vietnamese), all labels without language codes; e.g. English 443 cues 95.9–1435.6 s of 1450 s, 146 multiline |
| Jujutsu Kaisen (113415) ep 3 **Dub** | Unavailable after 3 sites, 66.8 s | Anikoto exact unit + MAL, selected host unavailable; Miruro and Re:Anime without matching identity | — | — | — |
| Dandadan (171018) ep 2 **Sub** | Unavailable after 3 sites, 66.7 s | Re:Anime reported a **conflicting** identity and was rejected | — | — | — |

Observations:

- **No provider declared an audio language**, in the resolver result, an HLS `EXT-X-MEDIA` group or the MPEG-TS program map. The app's "Sub/Dub server selected · audio language unverified" notice therefore stays correct for every native result here. The Whisper results are automated support for these 30-second samples only, not a human listening confirmation, full-episode coverage or provider metadata.
- **Anikoto caption files declare no language codes**, only labels. Within that provider, the remembered caption key (label-based) matches exactly across units. The new declared-language fallback has no effect on these files by design, because a display label is not treated as a declared language.
- The provider captions are full-length timed VTT files with many authored multiline cues, except the Frieren Dub file, which is partial (55 cues).
- Even the successful resolves reached Anikoto's third server after two "file unavailable" hosts. In each failed request the first server took about 20 seconds, so the per-site budget ended after one server. Deadlines were not changed; the unavailable outcomes stayed honest (no wrong unit or website fallback).

Receipts and sanitized logs: `live-run1/*.json|.log` in the evidence folder. The 30-second mono WAVs stay private (`live-private/`) only until a human check finishes.
