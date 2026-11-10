# Player and tab-strip faults after the merge — November 10, 2026

Branch **claude/player-ui-dpi-e60c33**, fast-forwarded to the merged **401155b**. Every fault below failed a check before its fix. Physical runs used AppFixture's isolated profile on the second monitor, under the shared desktop lock, released after each run. TRX files are in the worktree's `.artifacts/implementation/build/results`.

## The merged code

On the rebuilt merge, `NativePlayerDisplayTests` and the playing-controls check passed at once. The keyboard journey failed only at setup: it looked for the player tab by its old title, and after the merge that title follows the opened media. It now takes the last tab and passes (`second-pass-3-modified-arrows.trx`).

**Safety repair to the tests.** In one run the app could not take the foreground, so the journey's Tab presses went to the Claude desktop window. Focus moved over its Prompt, Model and Send controls; only Tab was sent, so nothing was typed or activated. Every physical key in the player tests now first requires the app to own the foreground window, and sends nothing otherwise. Pointer input also requires the app's window under the target point. While a button is held, that check does not activate any window, since activation cancels mouse capture.

## 1. Modified arrow keys sought the video

Ctrl+Shift+Left/Right moves the active tab. When the tab could not move further, `MainWindow` left the key unhandled, and the player's shortcut handler, which ignored modifiers, sought instead. Keys pressed inside VLC's overlay window never reach `MainWindow`, so any modified arrow there sought too.

- **Before** (`second-pass-1-merged.trx`): Ctrl+Shift+Right on the last tab moved the paused video from 60.0 s to 90.0 s.
- **Fix** (**f772f9a**): player shortcuts need an unmodified key.
- **After:** `ModifiedArrowKeysNeverSeekThePlayer` passes. The video stays put through Ctrl+Shift+Right/Left at both ends of the tab strip, and through Ctrl+Right, Shift+Left and Ctrl+Shift+Right inside the native controls. The tab still moves left, and a plain Right still seeks. `NativePlayerKeyboardTests` 3 / 3.

## 2. The tab strip dragged a tab from an earlier click

A press on a tab header remembered the tab and its start point, and only a drag cleared them. A plain click left the tab ready to drag: a later press anywhere else in the window, held and moved over a header, dragged the earlier-clicked tab and dropped it there.

- **Fix** (**5d11afd**): the drag decision is now `TabDragGesture`. `MainWindow` forwards window-level left presses and releases to it, so a release or any new press clears the old one.
- **Before** (`tab-drag-before.trx`, window hooks as no-ops as in the old code): "a click, then a press elsewhere" and "an unreleased header press, then a press elsewhere" both returned "Settings" as the tab to drag.
- **After** (`tab-drag-after.trx`): `TabStripInputTests` 3 / 3, including a held header press that drags once past the drag distance.

**Not qualified physically.** With injected input, the header press arrives (logged), but no button-held move ever reached the strip, even for a plain header drag of a utility tab. WPF reported the button released, with no capture, at the first move. Neither waiting for the UI thread, nor real `SendInput` move events, nor skipping activation mid-press changed that. The cause is not established, so the physical drag test was replaced by the decision checks, and a real drag remains to be checked by hand.

## 3. A new player tab opened at full volume

Every player started at volume 100, so after muting one tab the next played aloud (seen by the download-captions session).

- **Before** (`second-pass-volume-before.trx`): after the first player was set to 0, the second opened at 100.
- **Fix** (**ca93619**): a new player starts at the volume last chosen in any player during this session. It is not saved across restarts; whether a mute should outlive the session is a product choice.
- **After** (`second-pass-volume-after.trx`): 0, then 35, carries into the next new tab. Related: `PlaybackTabPauseTests` 7 / 7 and `RetainedPlayerTabsTests` 3 / 3. Its two-handle case missed its 4-second wait for the second player's surface once and passed alone on rerun (`second-pass-retained-rerun.trx`); that wait is unchanged.

## 4. `LiveNativeSelectorTests` ran out of clip

The journey plays its generated clip through speed, audio and caption steps whose UI Automation searches slow down on a busy desktop. On a normal run today it passed with the 60-second clip (`second-pass-selector-before.trx`), but only 6.1 s remained at the end. Slow runs reached the clip's end (59.4 s) before the last audio choice.

- **Fix** (**91b34f9**): the clip is 300 s, with the caption cue and encode deadline to match. A new final check requires at least 30 s of clip left, so a short fixture fails by naming the remaining time; every other assertion is unchanged.
- **Before** (`second-pass-selector-headroom-before.trx`): "Only 6.1 s of the clip remained after the journey."
- **After** (`second-pass-selector-after.trx`): 1 / 1 with 249.9 s left; speed ratios 1.766, 0.444 and 0.713.

## Still open

- A physical tab drag (see 2).
- Stable resources over repeated player journeys, a physical Arabic-layout keyboard journey, and real provider browser routes: left for a later turn.
- Mixed display scaling and Narrator speech: with the user.
