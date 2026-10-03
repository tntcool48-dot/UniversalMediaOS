# Python preparation implementation evidence

IP09 checkpoint, September 10, 2026.

The shared Python bootstrapper now exposes NotStarted, Preparing, Ready, Failed and Canceled snapshots. Availability checks read this state without launching processes. Concurrent callers share preparation; canceling one waiter does not cancel the owner. Explicit cancellation and disposal cancel owned work. A failed preparation can be retried in the same session.

Preparation has a two-minute owner budget across discovery, script deployment, imports, installation and syntax validation, with a bounded two-second process cleanup allowance. Each interpreter candidate has a three-second version probe. Both process output streams are drained concurrently; retained output is capped at 64 KiB per stream. Cancellation terminates the specific owned process tree and waits for exit/drain within the cleanup allowance.

Readiness requires Python 3, the three deployed scripts, successful actual imports and successful script syntax checks. Only missing packages are installed, and imports are checked again afterward. Pip exit failure, missing imports and script validation failure cannot produce Ready. Scraper engines receive a preparation exception containing the structured snapshot instead of reporting every failure as a missing Python executable.

Settings and the status bar use the bootstrapper snapshot rather than script-file existence. Settings offers Repair Python. Startup and Settings share the same enabled-unless-explicitly-false policy. Python preparation begins independently of FFmpeg/extension checks instead of waiting behind the extension download. The default configuration already supplies true; aligning blank/legacy values is not proof of the cause of the user's screenshot.

Validation includes fake process tests for sharing, canceled waiters, failed pip, failed recheck, retry, script revision changes, missing scripts, timeout and explicit cancellation. Real PowerShell process tests cover saturated stdout/stderr and owned-process termination. A local Python probe successfully imported DrissionPage, curl_cffi, httpx, bs4 and lxml, and parsed all three shipped scripts without starting browser or network work.

Remaining IP09 scope: runtime/package revision invalidation beyond script hashing; CLI protocol/version checks beyond syntax; complete app-shutdown await evidence; representative live scraper journeys and broader startup/repair UI acceptance. A Python installation is still required. This work does not install FFmpeg or resolve the deferred uBlock staging error, and does not establish that all services in the previously launched test profile are ready.

Reproduce the isolated build and suite:

```powershell
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/preparation-build
```
