# Dependency health and repair evidence

IP09/IP16 partial checkpoint, September 11, 2026.

## Diagnosis

The isolated manual profile `manual-test-20260910-221504` logged Python and FFmpeg unavailable at 22:15:04, then both available at 22:15:13. FFmpeg verification had completed before the extension download timed out. The screenshot is consistent with an early/stale health display; it does not establish that all services remained missing. Python preparation was addressed by the preceding checkpoint.

The older `manual-test-20260910-021932` profile recorded access denied on a uBlock staging directory. This failure was not reproduced. The current probe extracted and activated the official archive successfully. Transient rename retries mitigate temporary locks; they do not establish the root cause of the historical denial or bypass permanent permissions failures.

## Implementation

Dependency health events now update Settings/status health after individual checks, without waiting for the entire setup sequence. The existing Python repair control is now a single Repair services action: prepare Python, verify FFmpeg and retry uBlock. It does not install FFmpeg. Pending/preparing states are distinguished from failed checks. uBlock reports Installed rather than claiming browser loading has been verified.

FFmpeg readiness requires both ffmpeg and ffprobe to exit successfully with recognizable version output. The managed services directory takes precedence over absolute PATH entries, including quoted directories. Missing, failed, hung and unlaunchable tools report distinct diagnostics. Each probe has a three-second cancellation budget, using the existing bounded process runner and cleanup allowance.

Full dependency runs and extension installation are serialized per bootstrapper instance. Extension activation retries transient IO/access-denied failures up to three times with 100/200 ms delays. Renames must remain between sibling directories. Hash/version validation, bounded archive size, rollback and owned staging cleanup remain in place. The extension request budget is now 20 seconds instead of eight; extraction itself is synchronous and cancellation is checked before extraction/activation, so this is not a hard total install deadline. Failures identify their stage. Permanent permission failures remain visible and retryable.

## Evidence

- Ten added deterministic cases cover executable validation, managed/PATH selection, missing/hung tools, health publication before extension completion, bounded rename retries, cancellation/sibling guards and concurrent timed-out repair requests.
- A live isolated installer probe used the real bootstrapper and local FFmpeg processes, with the already downloaded official archive supplied through its HTTP seam. Both tools passed and uBlock 1.71.0 installed successfully. This verifies installation, not WebView2 loading or live playback.
- Official archive: [uBlock 1.71.0 release](https://github.com/gorhill/uBlock/releases/tag/1.71.0), 4,498,708 bytes, 775 entries. SHA-256 `5313a13fdbe748c23abdde6d24671635a3711a7ab0cf53f420bfa4aecdc36bf6` matches the unchanged production pin.
- Probe output: `.artifacts/implementation/dependency-qualification/installer-probe/result.json`. Probe source: `.artifacts/implementation/dependency-probe/`.
- The first full test run exposed an existing rapid-search UI Automation race: the second invocation was rejected because the async command had disabled its button. The test now accepts that specific input-gating exception and requires the button to become enabled again and results to remain available. Initial evidence is retained in `.artifacts/implementation/results/dependency-initial-e2e.trx`.

Final exact-build validation: **357 passed, 22 existing skips, zero failures**; zero build warnings/errors. Result: `.artifacts/implementation/results/dependency-e2e.trx`. Source/artifact hashes: `.artifacts/implementation/dependency-checkpoint.json`.

## Remaining acceptance

Live Settings repair and browser extension loading still need desktop acceptance. This batch does not change the already open manual app, select the final keyless non-TMDB metadata provider, fix upstream AniList availability, or establish movie playback correctness. Runtime/package revision invalidation, scraper CLI protocol checks and shutdown await evidence remain IP09 work. IP05/IP06 provider selection/migration and IP07/IP08 matching/resume remain open.

Reproduce the isolated build and suite:

```powershell
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/dependency-build
```
