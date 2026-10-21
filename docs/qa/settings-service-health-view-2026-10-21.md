# Settings service health view in the real app — October 21, 2026

Replaces the skipped `T1_Settings_03_System_Resource_Check_Status_View` placeholder from the [skipped-test inventory](skipped-test-inventory-2026-10-05.md). Branch **codex/app-recovery** after **267499f**; its own AppFixture profile on the second monitor, with automatic service management off (the fixture default).

## Case

Settings → Services lists all five local services: Python scraper, HLS proxy, uBlock Origin, FFmpeg and qBittorrent.

- The HLS proxy line is the deterministic one: it reads "Listening on 127.0.0.1:<port>", and an HTTP request to that endpoint gets an answer, so the line reflects a proxy that is actually running.
- After **Refresh**, the line still names the same running proxy.
- With services off, the observed lines were "Python scraper: NotStarted (not_started)", "uBlock Origin: Not checked." and "FFmpeg: Not checked.". The qBittorrent line depends on the machine. The test checks these lines are present, not their wording.

The first run failed on the test's own pattern, which expected an `http://` prefix and a trailing slash; the app shows `127.0.0.1:<port>`. After that fix the case passes in 8 s (`settings-service-health-20261021.trx`). The Settings and configuration classes pass **12**, with 2 Settings placeholders still skipped, in 1 min 29 s (`settings-class-health-after-20261021.trx`). Skipped methods in the suite drop from **12 to 11**: nine Downloads and two Settings.

## Limits

- No probe is injected, so the FFmpeg, uBlock and qBittorrent outcomes (found, missing, broken, hung) remain covered by `DependencyHealthTests`, not by this UI case.
- An HLS proxy that fails to start is not exercised here.
