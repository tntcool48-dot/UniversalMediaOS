# Native player keyboard, screen-reader text and display checks — October 23–24, 2026

Branch **claude/player-ui-dpi-e60c33**, cut from **e744dcd**. Checklist item: "Finish broader physical seek/selectors/shortcuts, supported sizes/themes/DPI/RTL and stable resources." Every physical run used generated local fixture media in AppFixture's isolated profile on the second monitor, under the shared desktop lock, released after each run. TRX files are in the worktree's `.artifacts/implementation/build/results`.

LibVLCSharp 3.9.7.1 draws the native player controls (`NativeOverlay`) in its own top-level window, "LibVLCSharp.WPF". That window is owned by the main window, is layered (per-pixel alpha), and is positioned in the main window's DIPs whenever the owner raises `LocationChanged` or a layout pass runs. All four faults below come from that arrangement.

## 1. A keyboard user could not reach the native player controls

WPF Tab navigation never crosses windows, so the controls were not in the main window's Tab order.

**Before the repair** (`keyboard-before-repair.trx`): from the selected player tab, 40 Tab presses cycled through the tab strip, window buttons and navigation twice and never reached a native control. Where the player should be, focus landed on its native video host, which UI Automation reports as the window itself.

**Repair** (`PlaybackView`):
- The player view is the main window's Tab stop for native playback (not for site playback), and its native video host no longer takes focus. A Tab that lands on the view activates the overlay window and focuses its first control; Shift+Tab focuses its last.
- The overlay contains Tab. At either end the view returns focus to the main window and continues to the next or previous control there. The overlay grid and the playback-state presenter are no longer invisible Tab stops. Each traversal request is `Wrapped`: otherwise, with no next stop and no way out of its presentation source, WPF restarts from the window's first stop and reports success.
- An open selector keeps its keys. Escape closes only that menu and leaves the selector focused; arrows, Home/End and letters choose in it. Before, the player took Escape first, closed the whole options panel and dropped focus, and Left/Right sought the video from inside a menu.
- Escape on a closed selector still closes Playback options (and leaves fullscreen or PiP), and now puts focus on their header instead of losing it.
- Left/Right on the focused seek bar use the player's −10/+30 s seeks, as their tooltips say; the slider's own step was 1 ms, so they did nothing. Its other keys step 10 s (Up/Down) and 60 s (Page Up/Down).
- Transport buttons are declared in reading order: Play, −10 s, +30 s, then volume, PiP, fullscreen. Tab and screen readers used to reach PiP and fullscreen before Play.

**After the repair** (`native-player-run-5.trx`, `NativePlayerKeyboardTests`, physical keys only after the fixture opens; 88 keys):
- Tab from the player tab enters at Playback options; then Seek video, Play or pause, −10 s, +30 s, volume, PiP, fullscreen.
- K resumes and pauses with a transport button focused. Right/Left on the seek bar seek +30/−10 s and stay paused.
- Space opens Playback options. Alt+Down opens the subtitle menu; Right/Left stay in it without seeking; Home and Enter commit CC Off; focus returns to the selector; the reload pauses again within 1.5 s of its position.
- Alt+Down, End, Escape closes only the menu and keeps CC Off. F4, End, Enter switches to the second audio track; Alt+Down, Down, Enter sets 1.25×.
- Escape closes Playback options with focus on their header, and Right still seeks.
- Tab past fullscreen leaves the player (to the window's first control, since the player is last in its order). Shift+Tab returns at fullscreen, walks back through every control, and leaves to the tab strip.

The intermediate physical runs are kept: run 1 (`native-player-run-1.trx`) found the two invisible stops and the wrap; run 4 (`native-player-run-4.trx`) found the wrap at the end once the menus passed.

FlaUI sends arrow keys as bare virtual keys. Under Alt, Windows reads that as keypad Alt-code entry, so Alt+Down never reached the app in runs 1–2 (F4 did). A physical arrow key is extended, so the test sends Alt+arrow as the extended scan code. This was a test-injection artifact, not an app fault.

## 2. Screen-reader text for the seek bar and TV selectors

A screen reader reads a slider's value pattern when it has one, otherwise its range value. It reads a closed selector through its selected item's peer, which falls back to the item's `ToString()` until the menu is first opened.

**Before the repair** (`screen-reader-before-repair.trx`, automation peers of the actual player view, in process):
- Seek bar: no value pattern, so only the raw range in milliseconds (61192).
- TV Season/Episode selectors (found by the catalog-dash-tv session): `AudiovisualSeasonChoice { Number = 1, DisplayName = Season 1 }` and a full `AudiovisualEpisodeChoice { Metadata = AudiovisualEpisodeMetadata { Id = … } … }` dump, instead of "Season 1" and "2. Cat's in the Bag...".
- The player's quality, subtitle and audio selectors already read their labels.

**Repair** (commit **45cb6a4**): the seek bar is a `PlaybackSeekSlider` whose peer adds a read-only value pattern with the displayed clock, "01:01 of 03:00" (Arabic "01:01 من 03:00"); the range value is unchanged for automation and seeking. The season and episode choices return their display names.

**After the repair:** `PlaybackScreenReaderTextTests` 2 / 2 with the related `PlaybackAccessibilityLocalizationTests`, `PlaybackSeekInteractionTests` and `CatalogViewModelPagingTests`: **49 / 49**, again on the final code (`player-inprocess-final.trx`).

These checks establish what the automation peers expose, not what Narrator says.

## 3. Hidden transport controls while playing

**Found by the catalog-dash-tv session:** after the 3-second idle hide during playback, the controls were collapsed and left the UI Automation tree. Reproduced in run 1 (`native-player-run-1.trx`): "The transport left the UI Automation tree while playing."

**Repair 1:** the idle hide fades the controls out and disables their hit testing without collapsing them; keyboard focus arriving in them shows them again. Run 3 (`native-player-run-3.trx`) passed that step and found a second, older fault.

**Found by run 3:** moving the pointer over the playing video did not bring the controls back. The overlay window's background was fully transparent, so the layered window passed the pointer through to VLC's video window and the reveal handler never ran.

**Repair 2:** the overlay background keeps an invisible alpha (`#01000000`), so the overlay receives the pointer over the video. The cursor hiding and the double-click/focus handlers already attached to the overlay now receive input too.

**After both repairs** (runs 4 and 5), on a solid-yellow fixture measured from screen pixels:
- after the idle hide the controls are invisible and still found by UI Automation;
- the pointer over the video shows them within 2 s, and they hide again;
- Invoke on the hidden Play/Pause (as a screen reader does) pauses, and the controls stay shown while paused.

## 4. Display placement, presentation modes and DPI

Both displays measured **100%** throughout (`DISPLAY1` 2560×1440 primary at (0,0); `DISPLAY2` 1920×1080 at (2560,362)). `NativePlayerDisplayTests` checks that the overlay window lies on LibVLCSharp's native video host window within one device pixel on every edge, has the owner's DPI (and the display's), contains the transport, and shows a rendered paused frame rather than a white or empty surface. It is written to run unchanged at mixed scaling.

**Found by run 2** (`native-player-run-2.trx`): moving the maximized player to the other display without a size change (what Win+Shift+arrow does between same-size displays) moved the video host to (312,289) but left the overlay window, with every control, at (2552,471) on the old display. WPF raises `LocationChanged` only for a restored window, and nothing triggered a layout pass.

**Repair:** the view watches the owner's `WM_MOVE` while maximized, runs its existing display-change handling (surface re-attach and paused-frame redraw when the display changed), and requests an arrange pass, after which LibVLCSharp realigns its overlay window.

**After the repair** (passed in runs 3, 4 and 6; the figures are run 3's, and later runs matched them), at 100% / 100%:

| Phase | Overlay window = video host (px) | Paused frame |
| --- | --- | --- |
| Maximized on DISPLAY2 | (2552,471) 1936×901 | white 8.1%, dark 13.5%, 23 colour bins |
| Moved to DISPLAY1 | (312,289) 1936×901 | same |
| Back on DISPLAY2 (resized to fit its work area) | (2580,499) 1880×845 | white 9.4% |
| Fullscreen | (2560,362) 1920×1080, the whole display | white 0.0% |
| After fullscreen, after PiP | (2552,471) 1936×901 | white 8.1% |
| Picture in picture | (3911,995) 546×376 | white 0.2% |

The position stayed at the paused 1.580 s throughout.

**Mixed scaling is not yet run.** It needs one display's Windows scaling changed, which only the user may do (see below).

## Related checks on the final code

- Run 4 (`native-player-run-4.trx`): `PlaybackPresentationTests` 3 / 3, `PlaybackTabPauseTests` 7 / 7, `RetainedPlayerTabsTests` 3 / 3, plus the playing-controls and display tests.
- Run 6 (`native-player-run-6-related.trx`), because the overlay now receives the pointer over the video: all 10 `LiveCaptionSelectionTests` variants (mouse dropdown and CC button; paused and playing; maximized and restored; English and Arabic), `PlaybackViewCollectibilityTests` and the display test: **12 / 12**.
- In process: 49 / 49 (above).
- Not run: `LiveNativeSelectorTests`. It already failed locally before this branch: its 60-second fixture runs out under slow UI Automation ([menu close QA](caption-menu-close-cause-2026-10-22.md)).

## Still open

- **Mixed Windows scaling.** Prepared: `NativePlayerDisplayTests` runs unchanged. It needs the user to set one display's scale (for example DISPLAY1 to 125%) while this session holds the desktop lock, then restore it.
- **Narrator speech.** These checks cover what the automation peers expose. What Narrator announces needs the user's permission to start Narrator and a person listening.
- **Stable resources across these journeys** (handles, memory, collectibility over repeated cycles) were not measured in this batch.
- Real provider browser routes remain unqualified. RTL was exercised only by the in-process Arabic peer text, not by a physical Arabic keyboard journey.
