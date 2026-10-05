# Native player resource cycles — October 5, 2026

Six actual Downloads → Breaking Bad S01E01 Pilot → native video → K pause → close-player cycles ran on the second monitor with the preserved isolated QA profile. Every opening rendered video and downloaded English cues. All six permanent download cards remained after closing each player. This is a finite native-library sample, not broad resource or release acceptance.

## Measurements

The reused Debug output was the published compact-Details checkpoint `7c78904`. WPF DLL SHA-256: `C4EBE92EB2DCF75146AB7A19F9714F81CB113EA5842B34352E4A38EE9A756376`. No player production code changed for these measurements.

Read-only process samples recorded private bytes, working set, handles, threads, accumulated CPU and owned descendants. No forced collection or working-set trim was used. Values below are rounded MiB; raw bytes/timestamps remain in `.artifacts/implementation/player-resources-20261005/samples.jsonl`.

| Stage | Private MiB | Working set MiB | Handles | Threads |
| --- | ---: | ---: | ---: | ---: |
| Downloads, before players | 184 | 247 | 1,865 | 40 |
| First player paused | 351 | 392 | 3,565 | 73 |
| First player closed | 281 | 328 | 2,480 | 30 |
| Sixth player paused | 363 | 405 | 3,588 | 64 |
| Sixth player closed | 298 | 341 | 2,550 | 35 |
| Sixth close, 66 seconds later | 232 | 335 | 2,529 | 25 |
| Later settled sample | 232 | 335 | 2,504 | 22 |

Closing players repeatedly released most native allocations and about 1,000 handles per opening. The sixth close subsequently released another 66 MiB of private memory naturally. Logs recorded native surface detachment, player stop/release and view/viewmodel disposal on every close. No owned child process remained in any sample. Residual memory and handles exceed the original startup sample; this does not prove absence of a leak or unlimited-cycle stability.

## Idle CPU and instrumentation

CPU continued increasing while Downloads was idle after the six closes. A 15-second local EventPipe trace and OS thread CPU deltas located repeated WPF accessibility provider navigation/dispatcher calls, rather than establishing retained decoder work. A fresh app touched by desktop inspection also showed this activity without opening any player.

A further isolated launch was left untouched by the desktop tool. Its accumulated CPU rose from **3.125 to 3.484 seconds over 48.031 seconds**, with the catalog visible and no players. Its separate 15-second trace primarily showed waiting threads. The earlier idle-CPU results are therefore materially affected by external accessibility inspection and cannot certify ordinary idle CPU or a player leak. Trace collection follows Microsoft's [high CPU diagnostics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-highcpu). Private traces were retained locally; no user logs, media, signed URLs or screenshots were published.

The diagnostic CLI is reused under `.artifacts/tools`; normal project build locations remain in use. Browser, mixed-player, download and reader resource cycles remain open.

## Selectors and preservation

The single audio choice correctly stayed disabled and the notice retained unverified spoken language. The first cycle's speed popup displayed its choices, but the desktop tool dismissed the popup and focused the source field before a 2× selection took effect. This is excluded from selector acceptance. The user-reported CC dropdown reversion remains unresolved; existing controlled mouse/direct-button cases do not establish its cause. Only the first cycle was explicitly muted; this batch does not qualify spoken audio.

Normal shutdown after the six cycles preserved all **six videos, 2,028,915,092 bytes**, caption sidecars and exact work/unit metadata. Pilot advanced from 718.160 to **782.923 seconds** through actual playback. S1E2 stayed **132.484**, S2E1 **371.022**, and Dune's feature **318.291 seconds**. No temporary jobs or unpublished copies remained. Configuration was unchanged at SHA-256 `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`.

A comparison launch initially used a misspelled isolation variable and briefly opened the default profile. It was closed normally immediately, then corrected to `UNIVERSAL_MEDIA_OS_DATA_ROOT`. No media/settings actions were taken in that launch. Default-profile configuration and database retained their July modification times; an empty queue was saved during shutdown. This launch is excluded from QA acceptance.

The subsequent Arabic layout investigation advanced only Pilot to 794.112 seconds before the localization repair. Its evidence and final profile restoration are recorded separately. All broader checklist groups remain open.
