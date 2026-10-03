# Anime canonical resume — October 2, 2026

Anime entries without a MAL ID previously saved progress under a normalized display title. Two different catalog entries with the same title could share progress, and changed display aliases could lose it. Catalog playback now captures the entry's AniList/MAL IDs and explicit episode in `EpisodePlaybackContext`, including native/browser streams, adjacent episode navigation and exact temporary-download handoffs.

Known anime catalog entries use `anime:anilist:<id>` and `episode:<number>` (an exact MAL ID is the fallback when AniList is unavailable). They reuse the ordered playback progress service added for Movie/TV. That service reads fresh SQLite state and keeps older paused tabs or delayed saves from overwriting newer progress. Anime progress does not create Movie/TV library entries. Standalone callers without catalog identity retain their existing behavior.

An exact MAL ID from the same catalog entry can import that episode's numeric legacy position only if the canonical row is absent and exactly one legacy row exists. The import logs the alias and retains every old row. A canonical completed position takes precedence. Title/URL aliases, unknown units and guessed episode numbers are not imported. No schema replacement, profile reset or deletion was performed.

## Physical checks

The current development executable was launched with the existing isolated Movie/TV QA profile and `StartupMonitor=Secondary`. All desktop input used the supported computer-use tool. The startup log confirms placement on the second monitor before display. Real user profiles and permanent downloads were untouched.

- AniList search **Frieren** → exact 2023 first-season entry, AniList **154587**, MAL **52991** → Sub → Stream episode → explicit Stream. EverythingMoe and The Index supplied 203 indexed providers; automatic routing used AniKoto. No static picker or website playback was used.
- Episode 1 displayed real native Direct3D11 video, English multiline captions and a 25:59 duration. Physical Right shortcuts sought forward. Returning to its tab retained the frame and stayed paused at **132.1 seconds** (log precision).
- A freshly resolved Dub stream opened in an independent native tab and restored **132.1 seconds**. Its position advanced to **183.661 seconds**. Closing the older Sub tab left that newer SQLite position intact. The options panel truthfully displayed **Dub server selected · audio language unverified**.
- Physical Next resolved episode 2 with Dub intent, started at its own beginning and saved **63.972 seconds**. Physical Previous resolved episode 1 again and restored **183.661 seconds**. The episode 2 row remained separate. English captions and actual video were visible in both samples.
- Normal application close while episode 1 was playing saved **213.262 seconds**; episode 2 remained **63.972 seconds**. The existing Dune / Breaking Bad film and two-season rows also remained unchanged.

- After the full regression/build, the same profile was relaunched on the second monitor. A fresh automatic Sub resolution restored episode 1 to **213.3 seconds** (the saved SQLite value was **213.262**); actual advancing video was observed and paused at **237.792 seconds**. Physical Next then resolved episode 2 and restored its independent **64.0-second** position (saved **63.972**), with actual advancing video visible. Both changed URLs and Sub/Dub choices retained catalog identity.

## Evidence and limits

The initial focused run passed **40 checks**, including ten new anime identity/progress cases and existing Movie/TV, retry and adjacent-navigation regressions. It covers same-title entries without MAL IDs, changed provider URLs/display titles, exact MAL import, legacy preservation, canonical completion, unknown units, older-tab close, audio intent and stream/download identity parity. Final full regression: **612 C# passes, zero failures, 22 unchanged skips** (634 total, 1 minute 52 seconds). Final WPF build: **zero warnings/errors**. The **156 Python passes** remain the previous baseline; Python was unchanged and not rerun.

[Full C# results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/anime-canonical-resume-2026-10-02.trx), [focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/anime-canonical-resume-focused-2026-10-01.trx). Final WPF DLL SHA-256: `E819670AD5D0B7348519AFB4D13B5ECD5DEA53D0BD74787786444C3E263DB2F2`; Core: `2148E72896ED1F298EB88BB98A722E271E9B3603AD87211EBBD5B27BD4DDCC21`.

Small evidence is under `.artifacts/implementation/anime-canonical-resume-20261002/`; build/test outputs remain in their existing locations. Raw isolated-profile logs contain media URLs and remain local. No Python source was changed.

These are scoped samples. Spoken audio, full-duration reliability, all providers, special/unknown provider-unit mapping, browser PiP, DPI/RTL/theme acceptance and real-profile migration/backup/rollback remain open. Caption language and provider audio flags do not independently establish spoken language. SQLite contention and bounded persistence under external writers remain tracked separately. This is not packaged-app or release acceptance.
