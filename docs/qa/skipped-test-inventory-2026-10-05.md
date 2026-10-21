# Existing skipped tests — October 5, 2026

The last full baseline (`browser-pip-full-2026-10-04.trx`) contains **22 skipped methods: nine Downloads, five Search and eight Settings**. The source attributes agree with the results. These are old UI placeholders that did not exercise the behavior in their names. They remain excluded from test totals; unskipping them unchanged would create misleading acceptance evidence.

This inventory distinguishes existing meaningful coverage from remaining gaps. It does not waive those gaps or complete the packaged release gate. Names below omit the common namespace `UniversalMediaOS.Tests.E2E.Tests`.

**Update, October 21, 2026:** five Settings and two Search placeholders are now real tests and no longer skipped, so **15** skipped methods remain: nine Downloads, three Search and three Settings. See the [Settings save/restart QA](settings-save-restart-and-protected-keys-2026-10-21.md), the [log boundary and lock collision QA](settings-log-boundary-and-lock-collision-2026-10-21.md) and the [search outage QA](search-outage-and-empty-results-2026-10-21.md).

## Downloads — nine

| Skipped method in `DownloadTests` | Existing evidence and remaining gap |
| --- | --- |
| `T1_Downloads_02_Pause_and_Resume_Download` | `DownloadQueueServiceTests.Queue_RunsOneJobAtATime_AndPausedPendingJobWaitsForResume` and `PauseActive_StopsExecutorBeforePersistingPausedState` exercise controlled execution and ordering. Actual preserved Downloads pause/resume and torrent behavior remain open. |
| `T1_Downloads_03_Download_Progress_Updates` | Season/library and playlist tests assert progress during controlled transfers; actual season saving has physical evidence. Queue UI progress with interruption and resumed torrent bytes remains open. |
| `T1_Downloads_04_Cancel_Download_and_Clean_Files` | `CancelActive_StopsExecutorAndRetainsHistory`, `TemporaryMediaWatchTests.CancellationAfterBytesArriveRemovesThePartialAndJob`, and `SeasonLibraryDownloadTests.CancellationBeforePublishRemovesOnlyTheOwnedStagingAndTemporaryJob` assert distinct ownership outcomes. Broader actual provider/magnet failure/cancellation remains open. |
| `T1_Downloads_05_Audio_Language_Preference_Selection` | `TemporaryWatchDiscoveryTests.SeededDualAudioSearchRunsEvenWhenDubFeedHasEnoughUnknownSeedCounts` and season tests reject unknown/conflicting requested audio. Real spoken Dub is still unqualified. |
| `T2_Downloads_01_Output_Path_Traversal_Attempt` | `TemporaryWatchDiscoveryTests.PublishingRejectsOutsideCaptionBeforeMovingAnyFile` checks a caption ownership boundary; season publication rejects invalid metadata and preserves other files. Arbitrary torrent output-path injection is not proved by those checks and remains a gap. |
| `T2_Downloads_02_Torrent_Client_Offline_on_Connect` | Dependency readiness and controlled transfer failures have coverage. Actual isolated qBittorrent outage and the visible queue outcome remain open. |
| `T2_Downloads_03_Disk_Space_Exhaustion` | October 6's native temporary-watch tests exercise real metadata/payload with injected initial/sidecar/falling capacity, stop local-error provider rotation and preserve unrelated files. Movie/TV direct/playlist/remux/publication capacity checks also pass. The old UI placeholder remains skipped; physical error-dialog, permanent season-torrent and OS write-failure acceptance remain open. [Current capacity QA](C:/Users/user/animeapp/docs/qa/download-capacity-stale-handoff-2026-10-06.md). The user's disk is not filled for testing. |
| `T2_Downloads_04_Malformed_Info_Hash_Magnet_Link` | Feed discovery excludes unusable magnets, but direct malformed-magnet submission is not established by the skipped method or those feed checks. Remaining gap. |
| `T2_Downloads_05_Interrupted_Download_Recovery` | `DownloadQueueServiceTests.LoadQueue_RecoversInterruptedRunningJobAsPaused` and temporary-watch restart tests assert orphan cleanup and active/unrelated-file retention. Actual queue/torrent restart/resume remains open. |

## Search — five (three still skipped)

| Skipped method in `SearchTests` | Existing evidence and remaining gap |
| --- | --- |
| `T1_Search_02_Search_Filter_By_Media_Type` | Catalog tests distinguish movie/series and exact identity, while the app exposes separate media areas. This old single-query test never selected a filter. Broader visible filter/identity acceptance remains open. |
| `T1_Search_03_Search_Result_Pagination` | `CatalogPagingTests` and `CatalogViewModelPagingTests` cover continuation validation, appending, late results and later-page failure. Actual second-monitor anime paging reached 108 items in the image-lifetime QA. Broader provider paging/relevance remains open. |
| `T1_Search_04_Fuzzy_Match_Score_Ordering` | Exact matching and conflicting identity rejection have dedicated coverage. No deterministic scored-order assertion exists in this method; fuzzy ordering is not claimed complete. |
| `T1_Search_05_Safe_Error_Display_On_Scraper_Offline` | **Replaced October 21 by a real test:** a 503 from the mock AniList shows "Anime catalog unavailable. Previous results retained; retry search.", keeps the cards and recovers on retry. Real network failures (timeouts, DNS) remain open. |
| `T2_Search_04_Scraper_Returns_Empty_Or_Null` | **Replaced October 21 by a real test:** an empty page shows "No anime found"; a reply without data shows "Anime catalog unavailable", not "No anime found". The unit coverage in `CatalogPagingTests` and `CatalogViewModelPagingTests` is unchanged. |

## Settings — eight (three still skipped)

| Skipped method in `SettingsTests` | Existing evidence and remaining gap |
| --- | --- |
| `T1_Settings_02_Set_Domain_Hot_Swap_URL` | **Replaced October 21 by a real test:** the scraper seed URL is edited, saved through the visible button and confirmation, and shown again after a normal close and relaunch. Live switching of an already-open provider tab is not claimed. |
| `T1_Settings_03_System_Resource_Check_Status_View` | `DependencyHealthTests` verify missing, broken and hung executable outcomes, and partial readiness. Deterministic resource/status UI and physical Health/Repair acceptance remain open. |
| `T1_Settings_05_Encrypt_and_Save_API_Keys` | **Replaced October 21 by a real test:** a Google Books key entered in Settings is stored as `dpapi:v1:` ciphertext, its plaintext appears nowhere in `config.json`, and it reads back. Unit coverage (`ConfigurationHardeningTests`, the `CoreHardeningRegressionTests` migration cases) is unchanged. Real-profile and packaged save/reload gates remain separate. |
| `T2_Settings_01_Duplicate_Service_Bootstrapping` | `DependencyHealthTests.ConcurrentExtensionRepairs_DoNotOverlapAndRemainRetryableAfterTimeout` covers extension repair coordination. Duplicate full service bootstrap/startup/shutdown is a distinct remaining gate. |
| `T2_Settings_02_Log_File_Rotation_Boundary` | **Replaced October 21 by a real test:** startup clears a log of 5 MiB + 1 byte and keeps one of exactly 5 MiB; Settings shows the size, and Clear debug log empties it. Growth within one session is still unbounded. |
| `T2_Settings_03_Settings_Lock_Collision` | **Replaced October 21 by a real test:** with `config.json` held without delete sharing, Save shows "Save Error" and leaves the file unchanged; the same Save succeeds after release. A new unit case keeps every key from 48 concurrent in-process writers. Writers in a second process on the same profile remain open. |
| `T2_Settings_04_Low_Memory_Resource_Boundary` | Resource samples and bounded image requests have evidence; no controlled low-memory boundary is established. Remaining gap. |
| `T2_Settings_05_DPAPI_Cryptography_Failure_Fallback` | **Replaced October 21 by a real test of decryption failure:** keys this account cannot decrypt (marked and legacy formats) are reported once, kept byte-identical through an untouched Save, and replaced only by a newly entered key, which is stored protected. Writing it found and fixed the untouched-Save erasure. Encryption failure itself is not injected; by inspection it throws before any setting changes, and its visible outcome remains open. |

## Tracker disposition

The inventory itself is complete. The remaining 15 placeholders (of the original 22) stay skipped and are never counted as passes. Remaining behavior belongs to the checklist's download, catalog/player, shared reliability and packaged-release rows. Replace specific placeholders with meaningful tests when addressing those behaviors; preserve an explicit exclusion for redundant methods rather than renaming a shallow check as acceptance.
