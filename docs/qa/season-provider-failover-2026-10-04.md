# Verified-source failover for TV seasons — October 4, 2026

## Actual problem and repair

The normal second-monitor Breaking Bad Season 1 retry reused its six completed episodes, then failed at S1E7 because both advertised HLS renditions contained empty resources. The catalog immediately stopped on this first source's download failure. The scraper also cancelled other extraction work after its first independently matched native result, so filtering that result only in the catalog could not expose another scraper source.

Season downloads now try at most three verified media candidates for the failed episode, sequentially. HTTP, truncated-body, invalid-media and timeout failures can trigger another bounded source lookup. Local storage/access failures and missing media tools stop the operation. Every candidate must independently match the immutable work/unit and requested audio; websites and unknown requested audio cannot become download success. Selection changes and cancellation prevent subsequent attempts and late publication.

Failed media origins/paths remain excluded for that episode's operation, including across providers. Rotating query credentials cannot retry the same path indefinitely. This conservatively excludes alternatives distinguished only by their query at the same origin/path. The exclusions reach the scraper before its first-match cancellation. A strict download request keeps discovery running past unknown requested audio; ordinary playback retains its early-result behavior. Each lookup keeps its two-minute ceiling; existing transfer/resource/disk checks and atomic publication still apply.

## Actual desktop result

All visible work used the second monitor, at (2880,478), 1280×800, and the preserved isolated Movie/TV QA profile. Normal build directories were reused.

- Before the repair, QA app PID 50904 found S1E7 at 04:08:27 and stopped at 04:09:10 after both renditions returned empty resources. Its visible status retained **6/7**.
- Final rebuilt QA app PID 47864 used TV Shows → My Library → Breaking Bad → Download season. All six saved episodes were reused. S1E7 lookup started at 04:19:36. At 04:20:16 the app continued after the first failed source, then at 04:20:56 continued after the second. The UI displayed “checking another verified source · 6/7 saved.”
- The third candidate also failed on empty resources. At 04:21:34 the bounded operation stopped. The visible final status reported S1E7's failure and **6/7 completed episodes kept**. This demonstrates failover and honest partial completion; it does not establish complete-season availability.
- After normal app close, read-only preservation checks confirmed all six videos and captions against their earlier identities and recorded sizes: **2,028,915,092 media bytes**, zero temporary jobs and zero unpublished copies. Canonical resume was unchanged: S1E1=450.434, S1E2=106.716, S2E1=371.022 and Dune feature=318.291 seconds. Real user profiles were not used.

[Small evidence](C:/Users/user/animeapp/.artifacts/implementation/catalog-season-20261004/) includes launch/build identities, sanitized operation logs, source-attempt counts, the final second-monitor observation/capture and a read-only preservation report. Earlier batch evidence was retained.

## Verification and limits

**88 focused tests passed**, zero failures/skips. Sixteen new cases cover replacement-source saves with separate units/progress, the three-candidate ceiling, rotated URLs, conflicting units/request echoes/unknown audio/websites, local failures, cancellation/selection changes, scraper exclusions and continued discovery for verified requested audio. Existing independent-match, source-evidence and provider-outcome checks also passed. [Focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/season-provider-failover-focused-2026-10-04.trx).

The final rebuilt app/tests compiled without warnings/errors. After the final desktop run, the **full C# suite passed 743 tests, zero failures and 22 existing skips, 765 total**, in 2 minutes 33 seconds. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/season-provider-failover-full-2026-10-04.trx). Python production source was unchanged; its latest recorded full result remains 161 passes.

The available S1E7 candidates still contain missing media. Exact catalog DASH Watch via download/native playback, complete seasons, spoken-language qualification and physical caption switching remain open. This batch introduces no new provider or relaxed identity/audio rule. Per-source limits are not an aggregate season byte budget; full-duration and packaged-app acceptance remain open.
