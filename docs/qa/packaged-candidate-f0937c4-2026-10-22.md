# Packaged candidate f0937c4 — October 22, 2026

Part of the release gate. Source **f0937c4** passed its full hosted checkpoint ([run 39727003308](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/39727003308)): **1,440 passed / 0 failed / 6 skipped of 1,446**, with a 16-minute test step inside the new 30-minute job limit; Python ran 229 tests. It replaces candidate [952d832](packaged-candidate-952d832-2026-10-22.md), because the merged caption-menu close logging changed the application. Only AppFixture sandbox profiles were used, on the second monitor.

## Package

- Before overwriting, candidate 952d832 in the reused publish folder still matched its manifest (`--verify`: unchanged).
- The folder was cleared, then a framework-dependent Release `dotnet publish` refilled it, in 8 s.
- `tools/write-release-manifest.py` recorded **1,362 files / 417,675,600 bytes**: 7 application, 40 managed dependencies, 968 native, 3 scripts and 344 assets. It also recorded **194** source inputs (one more: the menu diagnostics helper), deployed scripts matching their source, and the green checkpoint. Copies are `f0937c4-BUILD-IDENTITY.txt` and `f0937c4-manifest.json` in `.artifacts/implementation/package-smoke-20261009/`.
- Against 952d832, only the five application binaries changed; the SDK stamps the commit into every application assembly. Every dependency is byte-identical, and no file was added or removed.

## Packaged smoke

The same nine journeys as for 952d832 ran against the published executable (`UNIVERSAL_MEDIA_OS_EXE`): **9 / 9** in 1 min 19 s (`packaged-f0937c4-smoke-20261022.trx`). They cover:

- six startup and navigation cases;
- the close past the drain deadline;
- the pressured source admission;
- the second launch on the same profile.

Afterwards `--verify` reported the package **unchanged**.

## Limits

Not final release acceptance; the open gates are unchanged (real Windows sign-out, physical disk-full, broader providers and audio, Books). A fresh profile with services on was not repeated, because no Python script or dependency changed.
