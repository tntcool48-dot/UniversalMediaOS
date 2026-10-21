# Hosted checkpoint 0227ce0 and the caption dropdown variants — October 21–22, 2026

[Run 39666556906](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/39666556906) for **0227ce0**: one running app per profile, the 20-player drain ceilings, and the merged torrent file-entry containment check.

- **Result:** **1,431 passed / 1 failed / 10 skipped of 1,442** in 14 min.
- **New cases on the runner:** the second-launch case, the containment cases and the 20-player drain all passed.
- **Python step:** did not run, because the .NET step failed first. The node-timeout repair is still unconfirmed there.

## The hosted failure

`LiveCaptionSelectionTests.DownloadedCaptionOffAndOnRetainPositionAndPauseInNativeOverlay(paused: True, dropdown: True, restoredWindow: True, arabic: True)`: "CC Off did not restore the requested playback state."

The app's log shows the expected behavior within one second of the selection: the source reloaded without the caption, restored 1.9 s, and paused again. The test allowed **3 s** for the button, slider and decoder event to agree. The same run's TRX shows how long the passing variants needed from the start of the selection:

| Variant (on the runner) | CC Off | English (downloaded) |
| --- | --- | --- |
| Toggle button, playing | 0.18 s | 0.18 s |
| Toggle button, paused | 0.65 s | 0.87 s |
| Dropdown, playing (four variants) | 2.94–3.17 s | 2.94–3.06 s |
| Dropdown, paused, maximized, Arabic | 3.23 s | 3.61 s |
| Dropdown, paused, maximized, English | 4.00 s | 3.39 s |
| Dropdown, paused, restored, English | 3.87 s | 3.62 s |
| Dropdown, paused, restored, Arabic | failed | — |

## Local investigation of the dropdown variants

Repeating only the eight dropdown variants locally, with the test reporting its last observations, found four separate failure modes. Three of them are in the test:

1. **Too little time.** Local Off reloads also needed up to 4.7 s from the start of the selection. The reload wait's ceiling is now **10 s**, with unchanged conditions; each variant still logs its actual time.
2. **A zero baseline.** The position read before the change can return 0 when the slider is briefly absent from the UI tree. One failure wanted "-750 to 1,500 ms" while the slider had shown 1,651 ms just before the click, and the app correctly restored 1.7 s and paused at 2,092 ms. The test now waits for the slider's actual value.
3. **A label not yet translated.** In one Arabic run, the open list did not yet offer the Arabic "Off" label. The test now waits up to 3 s for the expected choice and lists what the menu offered if it never appears.

The fourth may be an app defect:

4. **The caption menu closing on its own.** In one paused English run, the menu opened, then closed by itself during the 2-second interval before the choice, about 7 s after the previous caption reload. No selection changed. The test detects this on purpose: a person reading the choices would see the menu close. This happened once in 64 variant runs with extra diagnostic snapshots enabled. The overlay's auto-hide (playing only, and not while the options panel is expanded) and focusing the video surface (mouse input only) are ruled out. The cause is not established.

Test-only diagnostics were added: snapshots right after a choice, active only when `UNIVERSAL_MEDIA_OS_CAPTION_DIAGNOSTIC_DIR` is set, and tolerant of the control briefly leaving the tree. The reload wait's failure message now states the last pause state, position and decoder event.

| Local run | Result | TRX |
| --- | --- | --- |
| Whole caption class, 10 s ceiling only | 9 / 10 (zero-baseline failure) | `caption-reload-ceiling-20261021.trx` |
| Paused dropdown variants × 6, with failure details | 23 / 24 (Off passed after 4.7 s; English then never selected) | `caption-arabic-paused-diagnosis-1…6.trx` |
| Dropdown variants × 6, with snapshots | 46 / 48 (zero baseline; a null in the old snapshot code) | `caption-dropdown-diagnosis-1…6.trx` |
| Dropdown variants × 8, baseline fix, with snapshots | 62 / 64 (untranslated label; menu closed by itself) | `caption-dropdown-baseline-1…8.trx` |
| Dropdown variants × 8, all test fixes, normal mode | **61 / 64**. Runs 2–8 passed 56 / 56. Run 1's three failures came back-to-back within 35 s, all maximized: an Off choice that did not take, the menu closing by itself, and the menu not opening. Then every later case passed. | `caption-dropdown-normal-1…8.trx` |

The clustered run-1 failures and the single earlier menu closure all involve the mouse-driven popup losing its open state or its click. These tests run on a shared desktop, where another window or notification taking focus closes a popup the same way, so environmental interference is plausible. An app cause is not ruled out: the menu-closing cause is still open and is proposed as its own investigation, with app-side logging of why the menu closed.
