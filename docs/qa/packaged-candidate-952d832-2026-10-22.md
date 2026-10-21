# Packaged candidate 952d832 — October 22, 2026

Part of the release gate. Source **952d832**, the first source in this run of batches to pass its full hosted checkpoint ([run 39678177654](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/39678177654): 1,432 passed / 0 failed / 10 skipped of 1,442; Python 229). Only AppFixture sandbox profiles were used, on the second monitor.

## Package

- Before overwriting, the previous candidate **2c83ca7** in the reused publish folder still matched its recorded manifest (`--verify`: unchanged).
- The folder was cleared, then a framework-dependent Release `dotnet publish` refilled it, in 9 s.
- `tools/write-release-manifest.py` recorded **1,362 files / 417,660,044 bytes**: 7 application, 40 managed dependencies, 968 native, 3 scripts and 344 assets. It also recorded **193** source inputs, deployed scripts matching their source, and the green checkpoint in `BUILD-IDENTITY.txt`. Copies are `952d832-BUILD-IDENTITY.txt` and `952d832-manifest.json` in `.artifacts/implementation/package-smoke-20261009/`.
- Against 2c83ca7, only the five application binaries changed (`UniversalMediaOS.Core.dll`/`.pdb`, `UniversalMediaOS.WPF.dll`/`.exe`/`.pdb`); every dependency is byte-identical, and no file was added or removed.

## Packaged smoke

The FlaUI journeys ran against the published executable (`UNIVERSAL_MEDIA_OS_EXE`): **9 / 9** in 1 min 17 s (`packaged-952d832-smoke-20261022.trx`):

- the six startup and navigation cases (Settings, Anime search, Movies, TV Shows, Cartoons, Books);
- the close past the drain deadline behind a held writer, restored after restart;
- the pressured film → episode admission;
- **new for this candidate:** a second launch of the packaged executable on the same profile, which restored the running window and exited with code 0 (one running app per profile).

Afterwards `--verify` reported the package **unchanged**.

## Limits

- **Not final release acceptance.** Other gates stay open: real Windows sign-out, physical disk-full, broader providers and audio, and Books.
- A fresh profile with services on was not repeated. It passed on 2c83ca7, and no Python script or dependency changed since.
