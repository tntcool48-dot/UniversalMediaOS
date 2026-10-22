# Why the caption menu closed by itself — October 22, 2026

Follow-up to the [0227ce0 checkpoint note](hosted-checkpoint-0227ce0-caption-reload-wait-2026-10-21.md). There, about 1 in 30–60 local runs of the dropdown caption variants saw the native player's subtitle menu close during the test's 2-second open interval, or saw a click on a choice not take. Branch **codex/app-recovery** after **ccc118a**.

**Result: environmental, not an app defect.** Another window taking the foreground on the shared desktop closes the menu in exactly the way the earlier runs recorded. The app now logs every open and close of this menu with the cause, which names the other process when that happens. No app change was needed beyond the logging.

## What the app now logs

`DropDownDiagnostics` (new, `UniversalMediaOS.WPF/Helpers`) is attached to the subtitle menu. It writes one line when the menu opens and one when it closes:

```
[Menu] Subtitle track menu opened: 2 choices, selected 'English (downloaded)'; its window active, foreground this app's ForegroundWindow.
[Menu] Subtitle track menu closed after 4658 ms; cause a choice was committed (Left mouse up on choice 'CC Off'); selected 'CC Off'; keyboard focus the menu box; foreground this app's ForegroundWindow.
```

If the close begins while WPF is still handling the person's input, that input is the cause: a committed choice, a click elsewhere or a key. This holds however long a choice's reload keeps the UI thread busy. Otherwise the cause is the earliest closing event seen while the menu was still open:

- its window was deactivated, naming the window that took activation (another process is named by process and window class, never by title);
- keyboard focus or Win32 focus moved, and to what;
- mouse capture moved, and to what;
- the menu was disabled or hidden.

When the close was not the person's input, the line also lists what happened while the menu was open. That includes rebuilt choices and selection changes, and events that followed the close itself, marked "(closing)". A close with none of these, such as UI Automation's Collapse, reads "no recorded cause". The player's "Native video surface attached" line now also names its trigger: view loaded, reloaded or shown, app window activated, display changed, or player changed.

## Evidence

**The app does not close the menu while it is idle.** WPF closes an open ComboBox only on input, focus leaving it, mouse capture moving away, or a click off its choices (`ComboBox.cs`, release/9.0). An in-process experiment confirmed the two suspected app paths. Clearing and refilling the choices while the menu is open (what `UpdateTrackOptions` does when the tracks change) keeps it open: focus moves to the menu box, and the selection stays cleared until the player sets it again. Reassigning the selection also keeps it open. Neither happens anyway while the test's player is paused: the downloaded caption choices compare equal across reloads, so `RefreshCaptionState` does not rebuild them. The video surface re-attaches only after the app window is activated, shown, reloaded or moved to another display.

**An outside focus steal reproduces the failure exactly.** With one paused variant running, a small probe window in another process took the foreground 0.7 s after the menu opened, and closed 0.6 s later. The test failed the same way as the earlier runs: "The caption menu closed during the open interval before CC Off". The menu ended unfocused (the earlier snapshot showed `Focused: false`) and playback stayed paused. The app logged:

```
[Menu] Subtitle track menu closed after 1011 ms; cause activation: its window was deactivated for powershell (pid 32888, WindowsForms10.Window.8.app.0.2335b1e_r8_ad1); selected 'English (downloaded)'; keyboard focus none; foreground powershell (pid 32888, …). While open: 853 ms activation its window was deactivated for powershell (…); 855 ms activation the app was deactivated for powershell (pid 32888); 856 ms focus its window lost Win32 focus to powershell (…); 856 ms capture released as the menu closed (closing); 857 ms focus moved from choice 'English (downloaded)' to none (closing).
```

The same mechanism explains the other two symptoms. A click aimed at a choice lands on whatever lies under it once the menu has closed. A click aimed at the menu box does not open it while another window covers that point.

The earlier three-failure cluster logged the app window being activated at the moment of the missed English choice. That fits an outside activation, or the click landing on the app window after the menu had already closed. The old log line did not say which. While the menu is open, nothing in the app activates a window. The app activates its window only for a second launch on the same profile, and LibVLCSharp's overlay focuses it only when the video view loads.

**Repeated runs with the logging** (dropdown variants, 8 per round; `.artifacts/caption-menu/results`):

| Runs | Cases | Result | Logged closes |
| --- | --- | --- | --- |
| `first-1`, `normal-1`…`normal-9` | 80 | 80 / 80 | 160, all "a choice was committed" |
| `normal-10` | 3 of 8 ran | 1 failure (playing, maximized, English) | lost; see below |
| `probe-steal-1` (one paused variant + probe) | 1 | 0 / 1, as designed | activation by the probe's process |
| `final-1`…`final-12` | 96 | 96 / 96 | 190 committed choices; 2 misattributed to "disabled" (see below) |
| `snap-1`…`snap-6` (with snapshots) | 48 | 48 / 48 | 96, all "a choice was committed" |
| `confirm-1`…`confirm-6` (after the attribution fix) | 48 | 48 / 48 | 96, all "a choice was committed" |
| `committed-1`, `committed-2` (the committed code) | 16 | 16 / 16 | 32, all "a choice was committed" |

In all, **288 / 288** dropdown cases passed in completed rounds. All 576 of their menu closes were committed choices; two were first logged as "disabled" (below). The one failure, in an interrupted round, lost its log.

The `normal-10` failure's evidence was lost. The round runner ran under PowerShell 5.1 with `$ErrorActionPreference = "Stop"`, and stopped at the first line the test wrote to stderr (the `[FAIL]` line). It never wrote the TRX, and the test's fixture then deleted the app log with its profile. The runner now runs `dotnet test` through `cmd`.

**Two committed choices were first logged as "disabled".** In two playing variants, the mouse-up on a choice came about 130 ms before the menu's own close began. In between, the choice's synchronous reload set `CaptionsAvailable = false`, which disables the menu. The first attribution looked back only 250 ms from the close, so it missed the input and named the disabling. The menu now notes which input WPF is handling, from its preview until it bubbles back to the menu. A close that begins during that input is attributed to it. A new in-process case blocks the selection for 400 ms and disables the menu, as the reload does; it read "disabled" before the change (`dropdown-diagnostics-before-input-fix.trx`) and reads "a choice was committed" after it.

## Changes

- `DropDownDiagnostics` attached to the subtitle menu (`x:Name="CaptionTrackMenu"`), logging through `AppLogger`. That is two lines per open-and-close; the list of events while open appears only for a close that was not the person's input. A closed menu does no work beyond a null check.
- `PlaybackView`: "Native video surface attached hwnd=X." now ends with "Trigger: …".
- `LiveCaptionSelectionTests`: the open-interval check and both "choice did not take" checks now report the app's `[Menu]` lines, and each dropdown variant writes them to its output. A future failure therefore states its cause.
- `DropDownDiagnosticsTests` (new, in-process, 4):
  - a committed choice;
  - a committed choice whose reload blocks for 400 ms and disables the menu;
  - another window taking activation, which names the other window;
  - choices rebuilt while open, which keeps the menu open, followed by a close without input, which names no cause.

  With the window-message hook removed, the activation and rebuilt-choices cases fail.

## Verification

All against an isolated Debug build in `.artifacts/caption-menu/build`, with `UNIVERSAL_MEDIA_OS_EXE` set; TRX files are in `.artifacts/caption-menu/results`. The committed code passed **19 / 19** (`caption-menu-committed-20261022.trx`) and the two `committed` rounds.

- **Dropdown variants:** the rounds above.
- **`DropDownDiagnosticsTests`:** **4 / 4**. The slow-choice case fails before the input fix (0 / 1). With the window-message hook removed, the activation and rebuilt-choices cases fail (`dropdown-diagnostics-hook-removed.trx`).
- **Related risks:**
  - `LiveCaptionSelectionTests` (all 10 variants, including both toggle-button ones): **10 / 10**.
  - `RetainedPlayerTabsTests` (it parses the "Native video surface attached hwnd=…" line): **3 / 3**.
  - `PlaybackViewCollectibilityTests` (closed views must still be collected with the diagnostics attached): **1 / 1**.
  - `PlaybackAccessibilityLocalizationTests` (the actual player's caption menu by name): **1 / 1**.
- **`LiveNativeSelectorTests` failed here, the same way with and without this change.** Its 60-second generated video runs out before the last audio choice: the log shows the player reaching 59.4 s and stopping, then the audio selection is empty. On this desktop today, its whole-window UI Automation searches take about 0.8 s, against 0.4 s when it last passed locally on October 8 (64 s then, about 1:38 now). Results:
  - this change: 0 / 3;
  - the base commit **ccc118a**, built from `git archive`: 1 pass and 1 failure at the same final step (`native-selector-base-ccc118a-1.trx`, `native-selector-base-ccc118a.trx`).

  The test never opens the subtitle menu. It passed on the hosted runner at 952d832.

## Follow-ups

- `LiveNativeSelectorTests`: a 60-second fixture leaves too little media when UI Automation is slow. A longer fixture (for example 120 s) would remove that dependency. Not changed here.
- AppFixture leaves the test runner's `media_os.db` files behind in `%TEMP%\UniversalMediaOS.Tests\AppFixture-*` (about 980 such folders on this machine). The sandbox is deleted while the runner's own SQLite pool still holds them.

## Limits

- The probe is a deliberate outside steal. The earlier natural failures were not recorded with this logging, so their exact source (the person at the desktop, a notification, or another program) is not known.
- Desktop focus steals still close a menu in a local run on a shared desktop. The test does not retry, so it still fails, but its message now says why. Hosted runners have no one at the desktop, and this failure has not appeared there.
