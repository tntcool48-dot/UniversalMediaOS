# Download capacity and obsolete anime handoffs

Date: October 6, 2026. Controlled production-path evidence, with isolated storage and generated media. Books remain deferred.

## Actual failures

The torrent temporary-watch path checked capacity only before transfer, counted video alone, and caught its local insufficient-space error as a provider failure. The reproduced low-capacity case tried the next torrent and eventually returned a generic matching failure. Cases with space for video but insufficient room for its sidecar, or with the reserve lost after payload arrived, still succeeded before repair.

Anime Details also captured a title/episode/audio choice without cancelling that operation when the selection changed. Three unchanged regressions deliberately returned a late search result after changing **title, episode or audio**. Each downloaded the generated native torrent, verified its video/audio and sent the **old** unit to the player message consumer. Each failed with one obsolete player handoff rather than the expected zero.

## Repairs

The native temporary-watch service now counts selected sidecars with the video, checks its 1 GiB reserve during polling and again before verification/publication, and propagates the local capacity error immediately. It stops the owning manager and cleans only its GUID job. It checks cancellation before publishing a completed cache entry. Provider rotation, title/season/episode/audio guards and final-owner leases remain intact.

Anime Details cancels its temporary watch when the title, episode or audio selection changes. An uncooperative late search result still encounters cancellation before transfer/handoff. A cancelled result cannot acquire a player lease; a result abandoned by the caller retains the existing lease cleanup.

An internal capacity callback makes the existing Movie/TV direct, playlist, sidecar and permanent-copy checks testable. Public production constructors continue reading the actual destination drive; no user setting, environment override, new disk-filling mechanism or provider option was introduced. The local exception remains an `IOException` and retains actionable free-space text.

## Verification and scope

**126 focused checks passed**, zero failures/skips, in **25 seconds**, with a zero-warning/error Release build. [Focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/download-capacity-and-stale-handoff-2026-10-06.trx). They include:

- A real localhost tracker/seeder, generated MKV with video/tone and English timed sidecar, production magnet metadata discovery, file selection and native payload transfer. Injected capacity rejected initial low space and insufficient sidecar space before payload; reserve loss after bytes arrived also stopped publication without provider rotation. The permanent sentinel survived and owned jobs were removed.
- Cancellation after actual native payload arrival, with only the owning temporary job removed.
- The three original selection-change regressions, now reporting cancellation and sending no obsolete player handoff. The intentionally late search callback ignored cancellation, so successful rejection did not depend on provider cooperation.
- Movie/TV direct-header rejection and capacity loss during an unknown-length body at the existing 64 MiB check; playlist rejection before payload, during a segment and before remux; permanent-publication rejection that retained the previously published episode and metadata. Failed cases did not reach probe/player publication and removed their own partial/staging data.
- Existing exact-unit/version separation, credentials, shared leases, cancellation during uncooperative probes/remux, Movie/TV stale/closed/cancelled callers, real DASH assembly and permanent-season failover regressions.

The pre-fix native capacity cases failed **3/3**; the pre-fix anime selection-change cases failed **3/3**. [Capacity reproduction](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/temporary-space-reproduction-2026-10-06.trx), [obsolete handoffs](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/anime-download-stale-selection-reproduction-2026-10-06.trx). The tests clean their own temporary fixture roots. The generated tone does not prove spoken Sub/Dub audio, and a synthetic catalog ID is not real-item qualification.

Capacity was injected at the production drive-query boundary: the user's disk was not filled or exhausted. These are service/viewmodel checks, not a new physical catalog/button/error-dialog acceptance run. Permanent season torrents, external qBittorrent, other processes consuming space between checks, filesystem permission/write failures and broad real-provider acceptance remain separate. The 22 old UI placeholders remain skipped; the new meaningful coverage does not unskip their unchanged bodies.

The final local full Release suite passed **881 tests, zero failures and 22 existing skips**, 903 total, in **7 minutes 7 seconds**, including the final production changes and 13 new boundary cases. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/download-capacity-full-2026-10-06.trx). Published queue checkpoint `b7bc3f3` passed [hosted validation](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37389160919): **868 passes/22 skips**, in **4 minutes 51 seconds**. It predates this capacity/selection batch. Python is unchanged, with its latest full result still **178 passes**.

Tested Release WPF SHA-256: `D18DC9F8D2C5EDBC92D1C09646643AB50AD69DD46F7661ADC2A973CDE26AE276`; Core: `A238347A67A59B0E3611CAB264276542F4CF1C5EAC612B1F0A4D361498DAE9B9`.

The read-only preservation probe retained all six originals (**2,028,915,092 bytes**), modification times, metadata/caption hashes and config, with separate original Pilot **803.205 s**, S1E2 **132.484 s**, S2E1 **371.022 s** and Dune **318.291 s** positions. Original profiles, Alice/imports and the user's Debug app were untouched. Existing builds were reused; no cleanup/reset or permanent media duplication occurred. The next physical work is Windows DPI/broader RTL. Arabic cartoons remain last among the non-Books tasks.
