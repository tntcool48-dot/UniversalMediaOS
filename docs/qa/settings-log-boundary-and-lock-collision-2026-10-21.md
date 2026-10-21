# Settings log boundary, Clear log and a save under a held config file — October 21, 2026

Part of the open Settings action states and the [skipped-test inventory](skipped-test-inventory-2026-10-05.md). Branch **codex/app-recovery** after **e632671**. Each UI case launches its own AppFixture profile on the second monitor.

## Cases

| Test | Journey | Result |
| --- | --- | --- |
| `T2_Settings_02_Log_File_Rotation_Boundary` | Two launches. First, an `app.log` of 5 MiB + 1 byte is cleared at startup: the first line is "Log cleared due to size constraints (exceeded 5MB).", the old content is gone, and Settings → Services shows a size in KB. Second, a log of exactly 5 MiB is kept: it still starts with the old content, and Services shows "Debug log size: 5.00 MB". In that profile, Clear debug log → "Clear Debug Log" Yes → "Logs" OK leaves "Log manually cleared." as the first line, and the shown size returns to KB. | Pass, 20 s |
| `T2_Settings_03_Settings_Lock_Collision` | Another handle holds `config.json` open for reading without delete sharing, as sync, backup or antivirus tools can. Saving a changed seed URL shows "Save Error". `config.json` stays byte-identical, and the log has "Failed to save config synchronously". Once the handle closes, the same Save shows "Settings Saved", stores the URL, and leaves no `config.json.tmp`. | Pass |
| `ConfigurationHardeningTests.ConcurrentWriters_KeepEveryIndependentSetting` | 48 concurrent writers mix batch saves, single writes and async writes, as Settings, token refresh and services do. Every key is present after a reload, and no temporary file remains. | Pass |

No app defect was found: these cases confirm existing behavior.

The first run of the log case failed because of the test's own message-box helper: it treated the follow-on "Logs" message as the confirmation still being open (`settings-log-and-lock-first-20261021.trx`). The helper was fixed; the app was not changed.

## Verification

The Settings and configuration classes pass **11**, with three Settings placeholders still skipped, in 1 min 23 s (`settings-log-and-lock-after-20261021.trx`). Skipped methods in the suite drop from **19 to 17**: nine Downloads, five Search and three Settings.

## Limits

- The log is bounded only at startup. A long session can grow past 5 MB until the next start or a manual clear. A runtime cap or rotation would be a product decision.
- Startup clearing discards the whole earlier log instead of keeping its newest part.
- A held `config.json` fails the save at once, with no retry. The user sees "Save Error" and can save again.
- Writers in other processes, such as a second app instance on the same profile, are not covered. Each instance writes its whole in-memory settings, so the last save wins.
