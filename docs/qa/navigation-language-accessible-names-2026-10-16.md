# Navigation and Language accessible names — October 16, 2026

Actual packaged **9d11668 /PID26208** ran in the existing private player QA profile on the second monitor, origin **2880,478**, window **1280x800**. Its visible Anime/Manga destinations exposed Names **Search/Library**. Settings → Language exposed an unnamed combo box despite its existing explanatory HelpText. These are observed accessibility mismatches, rather than merely source-review concerns.

The Anime/Manga names now bind to the same existing localized keys as their visible labels; Language gets its existing localized label as an explicit Name. No commands, layout, HelpText, reader/media behavior or settings persistence changed. Seven older navigation test locators use Anime/Manga after this deliberate Name change; their behavioral assertions/deadlines remain intact.

The single affected scope passed **three /zero failures/skips in36seconds**: actual Anime navigation, editable/wired Manga search, and the existing live English → Arabic → English localization case. The latter uniquely identifies the actual navigation peers by existing HelpText so an Anime tab cannot satisfy the Name assertion, verifies both navigation names and Language's explicit Name, and retains its unchanged saved-configuration assertion. These visible test fixtures use their existing Secondary startup setting and isolated profiles. Other already accepted Manga flows were not repeated for locator-only changes. The TRX is retained in `.artifacts/implementation/package-smoke-20261009/player-keyboard-dpi-20261016`.

## Native attempt and remaining physical limits

The same earlier package opened the existing permanent Sintel file without copying/downloading it. Volume was set to zero; native playback restored **78.858seconds**, rendered film frames and K paused at **87.319seconds**. English captions remained selected and speed stayed1x. Its private canonical film row saved87.319 on normal close. No new spoken-audio, timed-cue or tab-return qualification is claimed from this sample.

The speed popup visibly opened, but the desktop tool's Down action reactivated the shell and dismissed it with unchanged speed/paused frame. Indexed overlay slider/button actions also reported unavailable cached elements; coordinate drag/click succeeded. This is insufficient for physical popup keyboard commit/cancel acceptance and is not labeled an app regression. No identical popup attempt was repeated afterward.

One Windows Settings launch returned `launched app did not expose a targetable window`; a fresh window inventory still contained no Settings window. No OS scale was read or changed. Actual mixed-DPI/125% transition, popup keyboard input, broader Arabic accessible names, layout extremes and general screen-reader compatibility remain open. The finite three-name repair does not close the broad player/shared UI or release groups. The sanitized native attempt receipt is `native-ui-9d11668.json`; all original user profiles remain separate.

## Published candidate

Source **33d5bd4d2589ef0a51dd542482d167c653f415bc** is pushed normally to main and published once in the existing folder. Both assembly versions identify it; complete hash/file-set verification passed **189 runtime source inputs /1,365 published files**. The fifteen protected rows/configuration and separate anime205.461-second row/configuration remain unchanged. The private native UI sample's own87.319-second film row remains separate. Its single [automatic checkpoint](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/38944173041) completed failed: **1,357 passes /five SearchTests navigation-helper failures /22 unchanged skips /1,384 total**. The preceding drive-alias checkpoint38942483547 passed **1,362 /zero failures /22 unchanged skips /1,384 total**. Both original TRXs/summaries were retained once. No duplicate broad local run was performed.

## Missed search navigation helper — October17

All five hosted failures stopped at `SearchTests.NavigateTo` before search assertions: the old Name Search bound a non-invokable descendant after the deliberate navigation Name repair. FlaUI reported PatternNotSupportedException for Invoke; the exact matched descendant was not logged. These same five cases passed in the preceding source. The missed helper now selects an Anime Button by Name and ControlType. Actual search action locators, query/error/concurrency assertions, deadlines and five existing skip reasons remain unchanged.

One affected class run passed **five /zero failures /five unchanged skips /ten total**, reported duration **six seconds**. Hosted failures are the before evidence; no duplicate failure run or full local cohort ran. This is a test-only correction: the published33d5bd4 app and its189 production inputs are reused, with a new automatic checkpoint for the corrected harness. No product rebuild/republish is required solely for this helper.
