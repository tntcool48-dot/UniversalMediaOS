# Movie/TV desktop journey and resume — October 1, 2026

Actual catalog-to-native-player interaction now works on the second monitor for Dune (2021), Breaking Bad S1E1 and S2E1. This batch also repairs progress synchronization between independent player tabs, SQLite and the Movie/TV library. Recovery and packaged-app acceptance remain incomplete.

## Problems and fixes

The player persisted canonical unit positions to SQLite, but never called the library's progress method. Details therefore showed only an opening date. Opening another TV episode could retain the previous episode's position under the new episode number. Player-local ordering also allowed closing an older paused provider tab to overwrite a newer player's position; closing an older episode could replace the last watched unit's summary.

A shared Movie/TV progress service now captures immutable work/unit/position/duration writes, reads through a fresh database context, and orders saves across players. New provider sessions own their unit's progress. An older tab can regain ownership when explicitly played; returning to a paused tab alone does not. Separate work ownership protects the last watched unit's summary while retaining every unit's SQLite position. SQLite commits before the JSON summary, so a summary failure preserves authoritative resume data. Delayed observations cannot replace a newer summary. Final player flush waits at most two seconds; the captured operation owns its context and can finish after tab disposal.

Native callbacks queued before a media replacement or disposal are discarded. Time updates read the current native clock inside the dispatcher callback, protecting restored positions from queued pre-seek events. Completion uses the same generation guard. Existing anime identity/persistence behavior is retained in this batch.

Details refresh their resume summary on library progress changes. Reopening TV details restores the last saved season/episode only when the current catalog supplies one exact numbered unit. Missing, ambiguous and unestablished special numbering stay unselected; an absent season is never presented as Season 1. Opening a different unit clears only the library summary's position/duration, preserving the prior unit's SQLite row.

## Physical verification

The current WPF executable was launched with an isolated profile under `.artifacts/implementation/movie-tv-ui-resume-20261001/profile`. Startup logs verify secondary-monitor placement before display. All desktop inputs used the supported computer-use tool. Real profiles, permanent downloads and the modified working tree were preserved. Builds reused the existing Core/WPF/test output directories; no build copies were created. The separately completed storage cleanup was confirmed in `.artifacts/storage-audit-20261001/cleanup.json`.

- Wikidata search distinguished Dune (2021), Dune (1984) and Part Two. The 2021 item resolved an independently matched native Vidsrc source. Its own native tab displayed film frames and English timed captions, sought forward, entered/exited fullscreen, paused on departure, retained its frame/position on return and resumed on command.
- TVmaze search distinguished Breaking Bad from Original Minisodes. The chooser exposed Pilot as S1E1 (TVmaze episode 12192) and Seven Thirty-Seven as S2E1 (12199). Both opened separate native tabs with different opening scenes and durations. The pilot entered/exited fullscreen and returned paused at 306.160 seconds; S2E1 sought forward and paused at 64.846 seconds. The earlier film position remained separate.
- After normal application close, rebuild and restart with the same isolated profile, freshly resolved native URLs restored Dune to **157.561 seconds**, S1E1 to **306.160 seconds**, and S2E1 to **64.846 seconds**. Actual film/episode frames and English captions were observed. Subsequent native progress appeared in details as `Resume movie at 2:58` and `Resume S01E01 at 5:41`.
- The final build reopened the retained series with **Season 2 / 1. Seven Thirty-Seven** selected and **Resume S02E01 at 2:56** visible. No manual unit selection was needed. The final work-summary ownership fix was covered by the controlled older-episode-close regression; the earlier physical native restore samples preceded that last fix.

Sanitized [resume/monitor evidence](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-ui-resume-20261001/resume-evidence.log) retains only those log lines. Raw isolated-profile logs can contain signed media addresses and remain local.

## Automated verification and limits

The final focused run passed **82 tests**, covering changed provider IDs/URLs/titles, independent players, explicit older-tab resume, separate films/adjacent episodes/seasons/established specials, delayed saves, completion, summary write failure, queued callback replacement and exact saved episode selection. [Focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/movie-tv-resume-focused-2026-10-01.trx).

The final full run passed **602 C# tests, zero failures, 22 existing skips, 624 total**, in 2 minutes 9 seconds. Build: zero warnings/errors. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/movie-tv-ui-resume-2026-10-01.trx). Final WPF DLL SHA-256: `32639E21A2EC6EB1B7596811B198181B9A20F0959F32BBC72652F046065E6301`; Core DLL: `A316D69DD7F00FE18E0561152A8BB2CFC02C75EEEAFA61F531BB382F8CE53C0B`. Python production code was unchanged; the previous 156 passes remain the recorded Python baseline.

These are short native samples and controlled persistence regressions. Actual spoken audio, full-duration captions/reliability, other live providers, live adjacent/special mapping, visible Watch via download, HLS/DASH/season downloads, contention under external database locks and packaged/real-profile migration acceptance remain open. Audio language stayed unverified. Website fallbacks were displayed explicitly and were not used as Stream success. Desktop snapshots occasionally cached old state or rejected an input after user/window movement; those outcomes were reobserved and were not counted as application failures or successful actions.
