# Native caption Off/on — October 5, 2026

The user reported that selecting CC Off immediately returned to English. During the targeted investigation, desktop-tool clicks dismissed the native popup and focused the source field before any Off request reached the player. Temporary diagnostics confirmed that distinction; they were removed from production source. This explains the recorded automated failures, but does not establish the cause of the user's manual failure.

The player now has a direct **CC** button beside its existing language selector. It turns captions off without opening a popup and restores the last selected named language when turned on. A missing or ambiguous remembered choice stays off rather than selecting another language. Decoder-local unknown track IDs are forgotten when changing sources. Each player owns its own remembered choice. Existing native selection reloads retain position, pause intent, source context and caption/session ownership.

## Controlled verification

- The bound downloaded-caption selector passed before the change. Live generated-video accessibility selection and mouse selection also passed for playing and paused media before the change. These results do not reproduce or dismiss the manual report.
- The first paused overlay test failed while reading a stale automation window's Name, before caption selection. Using the returned optional property repaired the test's window lookup. It was an automation failure, not a caption failure.
- **25 focused checks passed**, zero failures/skips. Two launch the real app on the second monitor with a generated video and two-line saved sidecar, click the new CC button, and check Off/on, time and pause state after decoder refreshes. Additional checks cover bound selection, retry, remembered Arabic across reordered episodes and refusing to substitute English when Arabic is missing.
- [Focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/caption-toggle-focused-2026-10-05.trx). The final full suite passed **841 tests, zero failures, 22 existing skips, 863 total**, in 3 minutes 10 seconds. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/caption-toggle-full-2026-10-05.trx). The build emitted no warnings/errors. Python source is unchanged; its latest full suite remains 178 passes.

## Actual downloaded-media check

The production app used the preserved isolated Movie/TV QA profile, with services auto-management disabled and `StartupMonitor=Secondary`. Its returned screenshot origin was `(2880, 478)`, size `1280×800`. The actual journey was **Downloads → Breaking Bad S01E01 Pilot → Play → pause → Playback options → CC**.

At **702.768 seconds**, a two-line downloaded English cue was visible. One CC click selected **CC Off**, removed the cue and settled paused at **703.184 seconds**. Off stayed selected on a later observation. The next CC click restored **English (downloaded)** and the same two-line cue, settling paused at **703.602 seconds**. The native reload advances roughly 0.4 seconds while its first frame and pause settle; this is position retention near the requested time, not frame-exact pause preservation. Transport clearance followed caption visibility, and the video remained rendered.

Private screenshots and logs are retained under `.artifacts/implementation/player-selectors-20261004/`. No downloaded media or screenshots were added to Git. The profile configuration SHA-256 remains `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`. All **six permanent videos, 2,028,915,092 media bytes**, captions and exact work/unit metadata survived. S1E2 remains at **132.484s**, S2E1 at **371.022s**, and Dune's feature at **318.291s**; only the played S1E1 position changed. No temporary jobs or unpublished copies remained.

Tested WPF DLL SHA-256: `F89A55512AD90F355CBD3C275D1ECD63515BD6D37E7FC2A15DF9226F6FC7790F`; Core DLL: `C97B7494F0A2D099BBA49DF839FACD50EA72C9CB57384BE9DF14364748C84354`. These identify the local tested output; hosted validation and physical acceptance are separate evidence.

## Remaining limits

The existing dropdown remains available; the cause of the reported manual popup behavior is not yet established. Generated video has no spoken audio and does not qualify real provider identity, embedded captions, authored formatting, full-duration playback or release acceptance. Broader audio/quality/speed selectors, DPI/themes/RTL, resource cycles and packaged execution remain open in the sole checklist. Books remain deferred.
