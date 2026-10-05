# Newly opened views and Arabic Downloads — October 5, 2026

Selecting Arabic mirrored the app and translated existing controls, but opening Downloads afterward left its header and buttons in English. This was reproduced physically on the second monitor and by a real-app regression before the repair. Media titles remained the requested original titles.

## Repair

The old automatic translation listened for `Loaded` on the main window. WPF's direct Loaded event did not notify that handler when later views or virtualized items appeared. Localization now inherits an instance Loaded subscription into descendants of the opted-in window. Each newly loaded element translates its static text without walking the whole tree again or replacing data bindings. Existing language changes still update open windows. Microsoft's [WPF lifetime event documentation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/events/object-lifetime-events) describes the Loaded event's direct routing.

Downloads now binds its Play/Read action labels to the existing localization source, retaining the same commands and file identities. Its header, explanatory text, toolbar, empty/queue states and relevant tooltips have Arabic strings. Settings' native saved-confirmation uses translated text explicitly. Separate caches preserve a control's original caption and tooltip; changing languages no longer substitutes the short caption for its descriptive tooltip.

## Verification

The final real-app regression passed Arabic selection → newly opened Downloads → translated header, toolbar and per-file actions → Settings → English → restored actions and descriptive tooltip. The generated one-byte `.mkv` is a listing fixture and is never played; this test does not qualify media decoding. [Focused result](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/localization-final-2026-10-05.trx).

The initial regression failed on the untranslated Downloads header. During repair, the test's English-only Language locator also needed to follow the now-translated navigation label; this was a test locator failure. The final test passed both directions.

Build: zero warnings/errors. Tested WPF DLL SHA-256: `2ED92BCC72C7F088A438275A3276CF4DEC98CA10B980FD36F77190ECEC35B351`; Core DLL: `F7603785118EFA259539BF723D984F12E13841A3E147F394ECE9B85071EACF6C`.

The final full suite passed **851 C# tests, zero failures, 22 existing skips, 873 total**, in 4 minutes 32 seconds. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/localization-full-2026-10-05.trx). Python source is unchanged; its latest full result remains 178 passes. Full test counts are separate from broad physical/release acceptance.

## Actual preserved-media journey

The repaired Debug app restarted using the existing isolated QA profile with its previously saved Arabic setting. It appeared on monitor two at `(2880,478)`, `1280×800`. Newly opened Downloads showed **التنزيلات**, **تحديث**, **فتح المجلد**, **حذف** and **تشغيل**, with all six original episode titles retained.

The first translated Play action opened the same permanent **Breaking Bad S01E01 Pilot** file, restored **794.112 seconds**, decoded video and rendered its English downloaded cue. K paused it at **803.205 seconds**. Switching to Downloads and back retained that frame, cue and paused state. Native video/transport remained left-to-right within the surrounding Arabic layout, and the caption retained clearance below the transport. No audio-language qualification is inferred from its English captions.

The QA app closed normally. The temporary Arabic configuration was checked before restoring the original bytes, SHA-256 `9751E42596DC78EFB8588993C98766F79536DDB097EEFB6115B894C37C7EE6B5`. All **six videos, 2,028,915,092 bytes**, sidecars and work/unit metadata remain intact. Only played Pilot advanced; S1E2 remains **132.484**, S2E1 **371.022**, and Dune's feature **318.291 seconds**. No temporary jobs or unpublished copies remained. Private evidence is under `.artifacts/implementation/player-resources-20261005/`.

## Limits and next work

This closes the demonstrated late-view translation defect and provides a scoped Arabic Downloads/native-player sample. Catalog data, dynamic statuses, tab titles and untranslated strings still require broader localization acceptance. Other sizes, actual Windows DPI values, caption/speed popup behavior, browser/provider paths and packaged startup remain open. Six earlier native close cycles have [separate measured resource evidence](C:/Users/user/animeapp/docs/qa/native-player-resource-cycles-2026-10-05.md); they do not certify unrestricted resource stability. The CC dropdown report remains unresolved. Books remain deferred and Arabic cartoons last among non-Books work.
