# Storage investigation and checklist cleanup — October 1, 2026

The workspace's apparent app bloat comes predominantly from repeated recovery/test/probe builds, each copying native playback dependencies. Application data is a small part of the measured footprint.

## Measured footprint before cleanup

Sizes below are logical file bytes, expressed in decimal GB. The workspace is 58.677 GB, or 54.647 GiB. The scan covered 219,317 workspace files with no access errors or skipped links.

| Location | GB |
| --- | ---: |
| Entire workspace | 58.677 |
| `.artifacts` | 53.577 |
| WPF project, including all builds | 1.870 |
| Test project, including builds/results | 1.199 |
| `.agents` | 0.723 |
| `outputs` | 0.690 |
| Git history | 0.565 |
| Normal local app data | 0.224 |
| Normal roaming app data | 0.018 |

There are 119 generated `bin`/`obj` directories accounting for 42.947 GB, plus 24 copied build-output folders accounting for another 9.914 GB. Generated outputs total 52.861 GB. Many old full build batches occupy approximately 0.85 GB each. Repeated LibVLC/runtime copies accumulate even though the source is relatively small.

The conservative cleanup preview selects **51.234 GB (47.715 GiB), 177,657 files**, in 853 generated folders/branches. It preserves source, the current project builds, profiles, downloaded media and QA evidence. Some older test builds contain `ui-evidence` screenshots, so the cleanup retains their existing paths and only selects surrounding generated branches. It also retains compiler/output files in those protected ancestor folders. Old published outputs and Git history are outside the cleanup scope.

## Cleanup status and manual action

**Cleanup completed after the user's explicit instruction to run it.** `tools/cleanup-old-builds.ps1 -Apply` exited successfully: **853 obsolete generated folders/branches deleted, zero failures**, and measured C: free space increased by **51.405 GB**. Current C: free space was **69.507 GB** at the final check.

The post-cleanup workspace scan found **7.453 GB (6.941 GiB), 41,675 files, zero access errors**. `.artifacts` is now **3.066 GB**; WPF/test project folders remain 1.870/1.199 GB. Preserved evidence, profiles and protected build-ancestor files explain the retained artifacts. No further cleanup was attempted.

Earlier bulk and single-folder deletion commands were rejected by automatic approval review with `blocked by policy`. The later user-authorized script execution was accepted; no alternate deletion mechanism was used.

The one-time [cleanup script](C:/Users/user/animeapp/tools/cleanup-old-builds.ps1) defaults to preview. Both its preview and actual execution validate workspace ownership, exact paths within `.artifacts`/`.agents`, absence of tracked files/links, source/media/evidence exclusions and running processes. The following command was executed:

```powershell
cd C:\Users\user\animeapp
.\tools\cleanup-old-builds.ps1 -Apply
```

The script recorded [actual results](C:/Users/user/animeapp/.artifacts/storage-audit-20261001/cleanup.json). The [inventory](C:/Users/user/animeapp/.artifacts/storage-audit-20261001/inventory.json), preview targets, [measured preview](C:/Users/user/animeapp/.artifacts/storage-audit-20261001/preview-summary.json) and [post-cleanup scan](C:/Users/user/animeapp/.artifacts/storage-audit-20261001/after-summary.json) are retained beside it. The preview estimates logical bytes; actual reclaimed space differs with allocation and concurrent system activity.

For future work, reuse the current project build folders or the runner's existing fixed artifact root. Store dated logs, result summaries and screenshots without creating another complete binary tree for each probe. The runner already supports a fixed build root; no application architecture change was needed.

## Checklist cleanup and verification

- Active checklist reduced from **7,842 to 1,323 words** (about 83%).
- **23 grouped open tasks and 9 completed-step summaries** replace 39 overlapping open entries and the long history.
- Removed duplicate summaries, repeated test totals, superseded failure claims, already implemented progressive/source-preference tasks described as open, duplicate release gates and internal bookkeeping without a user-visible outcome.
- Kept all substantive remaining player/media/download/book/cartoon/service/performance/data/package outcomes. Detailed history is in [the archive](C:/Users/user/animeapp/docs/qa/implementation-checklist-history-2026-10-01.md). No feature was marked complete by editing the document.
- Updated the clean-chat handoff to point at the cleaned checklist and record the completed storage cleanup.

No production source changed and no app was launched. Current WPF DLL SHA-256 remains `E5FF2C20A210BF34B36D538E736C04DB988DAD7DC21F3875AADDF2A60B777AD2`; Core remains `C9B074764EC79418A2A786961B1AACC45EFB6F09C422A8F94A29B9673EA9A2CA`. The current app executable, x64 LibVLC/libvlccore DLLs, test assembly, latest native-probe source/log and an old build's protected UI screenshot all remain present. No application tests were rerun for documentation/storage cleanup. The last recorded application baseline remains 580 C# passes, 22 skips and 156 Python passes.
