# Visible Movie/TV download feedback — October 11, 2026

Actual second-monitor **a89fbe5 / PID 12424** Movies → Sintel → Watch via download started an app-owned original MKV transfer. With six source cards, the old progress/status/Cancel block sat below the whole source list. At the normal **1280 × 800** window, the visible card still looked ready to start downloading and no transfer feedback or Cancel was visible. The accessibility document exposed advancing percentages, and a read-only handle confirmed bytes accumulating; this was a layout problem, not evidence of a stopped transfer.

The existing controls now occupy an automatic-height bottom row outside the details ScrollViewer. Progress and Cancel remain available regardless of source-list scroll position; completion/failure/cancellation status uses the same viewmodel bindings. An empty status collapses the row. The source list retains the remaining viewport and its normal scroll behavior. Download requests, ownership, source matching, progress callbacks and cleanup are unchanged.

The reused Release WPF build passed **zero warnings/errors in 3.44 seconds**, and the diff whitespace check passed. No new low-impact layout-mirroring test or broad local suite was written/run. The current a89fbe5 package continues its existing transfer; it excludes this layout repair. Updated-package physical progress/Cancel/completion layout is pending, and no grouped gate closes for the build alone. The next source checkpoint will cover this single layout batch; the already completed a89fbe5 checkpoint is separate.

An apparent enabled Auto next toggle was checked against the actual accessibility state and existing XAML: it was already **disabled** when Next was unavailable. No player behavior change is retained for that mistaken interpretation.

The current transfer remains app-owned under the private profile. The original profiles/media and six unrelated untracked paths are preserved. Reuse the existing publish folder after the running app and its transfer/players have finished normally.
