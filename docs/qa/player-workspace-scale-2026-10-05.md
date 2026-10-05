# Retained player controls and workspace zoom — October 5, 2026

At the supported 900×560 window size, returning from Settings at 140% workspace zoom left the native player's controls enlarged. Playback options showed only the tops of the speed/audio/subtitle inputs, while the paused Pilot frame and two-line English caption remained visible. All physical work used the second monitor and the existing isolated Movie/TV profile.

## Problem and repair

The workspace `LayoutTransform` belonged to the common `PlaybackTabContentHost`. Hidden player views remain children of that host to retain their native HWNDs and browser documents, so they also inherited the scale while Settings was active. Returning to a player correctly selected a nominal scale of 1, but LibVLC's separate foreground overlay retained its previous scale.

Workspace scaling now belongs to the ordinary catalog/utility presenter inside the host. Retained player views have no scaled common ancestor. Existing scale selection still excludes manga and book readers; player tab visibility, native handles, pause policy and disposal are unchanged.

## Verification

Two production-app regressions exercise Settings zoom changes and tab return at 900×560. Before the repair, a 34-pixel playback button returned at 31 pixels for 90% zoom and 48 pixels for 140% zoom. Both cases passed after the repair and also check the compact speed selector's bounds. The test uses isolated AppFixture profiles configured for the secondary monitor.

The focused set passed **13 tests**, zero failures/skips: those two cases, retained native tab handles/closure, downloaded-caption Off/on while playing/paused, compact controls/navigation and light/dark rendering. Build: zero warnings/errors. [Focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/player-zoom-focused-2026-10-05.trx), [before-repair results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/player-zoom-red-2026-10-05.trx).

The full suite passed **843 C# tests, zero failures, 22 existing skips, 865 total**, in 3 minutes 34 seconds. Python source is unchanged; its latest full suite remains 178 passes. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/player-zoom-full-2026-10-05.trx). Tested WPF DLL SHA-256: `C5B4BB2956D6CC1F5DE2E48937056C4D226BD65313671DBC758B7064BBEEFEDA`; Core DLL: `BC1FD288F57DA7B3AA04221F453C4F0D3E7E314C4EE65CFFBAC63C274DAB71C6`.

## Actual second-monitor checks

The final tested build opened the permanent **Breaking Bad S01E01 Pilot** from Downloads, resumed near the saved position, and was muted/paused. After visiting light-theme Settings at 140% workspace zoom and resizing to **900×560**, the retained player returned paused with full speed/audio/CC/subtitle/source controls, transport and a two-line downloaded English cue visible. The old enlarged/clipped options did not recur. CC Off removed the cue and stayed Off; the next click restored English and the cue, settling paused at **718.160 seconds**. The last reload drift was 0.375 seconds, consistent with the separately documented caption first-frame/pause limitation.

Selecting Arabic in the actual Settings picker mirrored shell/navigation and translated the available labels. Settings Save displayed its owned confirmation on the second monitor and wrote **Arabic / 140% / light**, retaining Secondary startup and disabled service auto-management. After normal process close/restart, the shell was still Arabic/light and Settings showed 140% with the increase button disabled. The player remained usable with translated transport labels before restart. This is scoped language/appearance save/reload evidence, not complete Arabic translation or all Settings acceptance.

Screenshot origins remained **(2880, 478)** on the second monitor; final samples used 900×560 and 1280×800. Small private evidence is under `.artifacts/implementation/player-selectors-20261004/`, including the before/after minimum layouts and saved/restarted Arabic Settings. The desktop tool's drag with a screenshot ID did not resize the later window; a fresh observed drag relative to the window succeeded. Failed drags are not resize evidence.

All **six permanent videos, 2,028,915,092 media bytes**, captions and exact work/unit metadata remain. S1E2 **132.484s**, S2E1 **371.022s**, and Dune feature **318.291s** are unchanged; only the played S1E1 position advanced. No temporary jobs/unpublished copies remained. The isolated profile's original configuration was restored byte-for-byte after the check: SHA-256 `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`. Both owned QA app processes closed normally. Real user configuration was never used.

## Limits

Workspace zoom is an app setting, not a change to Windows display DPI. Actual monitor/DPI transitions, broader language/theme/density combinations, sustained resource cycles and release acceptance remain open. The reported caption-dropdown reversal remains unresolved; the direct CC button has separate qualified Off/on evidence. Visible follow-ups include clipped Providers & Scrapers navigation text at supported sizes/scale and untranslated English labels in Arabic mode. The light-theme anime tag chips also retain a dark surface with dark text; their accessibility is not accepted. This repair does not qualify provider identity, spoken audio or unavailable downloads.
