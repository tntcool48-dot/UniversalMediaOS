# Release manifest tool and packaged candidate 1919e3e — October 21, 2026

Part of the release gate ("final source/script/asset/native-dependency manifest and readable build identity; smoke-test the packaged executable"). Branch **codex/app-recovery** at **1919e3e**, not pushed. Only AppFixture sandbox profiles were used.

## A tracked manifest tool replaces one-off scripts

Each earlier candidate's identity and manifest came from a one-off script kept in `.artifacts`. There were seven near-duplicates, and the identity text was edited by hand: the 33d5bd4 text had run-together words. `tools/write-release-manifest.py` is now the single, tested way to record a candidate:

- **Refuses:**
  - uncommitted tracked application inputs;
  - application assemblies without the HEAD commit that the SDK stamps into their version (`1.0.0+<sha>`), which proves the package was built from this source;
  - a deployed Python script that differs from its `UniversalMediaOS.Core` source.
- **Records:**
  - every tracked application input with its SHA-256;
  - every published file with its SHA-256 and a category (application, managed dependency, native, script, symbols, asset), decided from the file's own PE header (CLR directory) rather than its name;
  - the runtime frameworks from `runtimeconfig.json`, the pinned SDK, the hosted checkpoint text and the external runtimes that are not bundled.
- **Writes** a concise generated `BUILD-IDENTITY.txt` and `source-build-manifest.json` into the package, plus copies under the evidence folder.
- **Verifies:** `--verify <manifest>` lists every missing, unlisted or changed published file.

`tools/tests/test_release_manifest.py` covers:

- classification from synthetic PE32 and PE32+ headers, including a non-PE `.dll`;
- script mismatch and a script with no source;
- the commit stamp;
- every verify difference, where regenerating the identity is not a difference.

**4 / 4** pass, and the whole Python suite **229 / 229**, which CI now runs.

## Candidate 1919e3e

`dotnet publish` (Release, framework-dependent) into the reused, first cleared, `UniversalMediaOS.WPF/bin/Release/net9.0-windows10.0.17763.0/publish` took 4.5 s.

- **Size:** **1,362 files / 417,650,260 bytes**: 7 application, 40 managed dependencies, 968 native, 3 scripts and 344 assets (LibVLC Lua, web-interface and icon files for x86, x64 and arm64). The identity and manifest copies are `1919e3e-BUILD-IDENTITY.txt` and `1919e3e-manifest.json` in `.artifacts/implementation/package-smoke-20261009/`.
- **Against the previous 33d5bd4 package:** only the 5 application binaries changed, and every dependency is byte-identical. Source inputs grew from 189 to 191, the two journal files. The old folder also held stale `UniversalMediaOS.Core.deps.json` and `.runtimeconfig.json` files that a clean publish no longer produces; clearing the folder removed them.
- **Before overwriting,** the 33d5bd4 package was confirmed byte-identical to its recorded manifest and used for the rollback check ([rollback QA](rollback-compatibility-and-journal-replay-2026-10-21.md)).

## Packaged smoke

The existing FlaUI journeys ran against the published executable (`UNIVERSAL_MEDIA_OS_EXE`), with isolated sandbox profiles on the second monitor: **8 / 8** in 72 s (`packaged-1919e3e-smoke-20261021.trx`).

- Six startup/navigation cases: Settings, Anime search, Movies, TV Shows, Cartoons and Books workspaces.
- The real close-past-the-drain-deadline journey behind a held writer, 50 s, which logged "journaled" and restored after restart.
- The pressured film → episode admission journey, 13 s.

Afterwards `--verify` reported **unchanged**: all 1,362 published files kept their recorded hashes.

## Limits

- **Not final release acceptance.** The source still needs its single hosted checkpoint after the user pushes (identity: "Hosted checkpoint: pending"), and the remaining gates stay open.
- **Not run:**
  - a fresh profile with services on (Python preparation, dependency download), qualified on earlier candidates; no service code changed since;
  - a real Windows sign-out (prepared, awaiting the user);
  - broader blocking efficacy and external-runtime limits.
