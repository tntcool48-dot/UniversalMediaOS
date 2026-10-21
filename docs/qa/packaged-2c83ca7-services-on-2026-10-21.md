# Packaged candidate 2c83ca7 with services on — October 21, 2026

This candidate replaces [1919e3e](release-manifest-tool-and-candidate-1919e3e-2026-10-21.md), because the journal-write fallback changed application source. It was published the same way: cleared folder, Release, framework-dependent. `tools/write-release-manifest.py` then recorded it:

- **1,362 files / 417,651,424 bytes** in the same categories;
- assemblies stamped `1.0.0+2c83ca7…`;
- copies `2c83ca7-BUILD-IDENTITY.txt` and `2c83ca7-manifest.json` in `.artifacts/implementation/package-smoke-20261009/`.

The 8 packaged FlaUI journeys (navigation, close past the drain deadline, pressured admission) passed again against it: **8 / 8**, 1 min 13 s (`packaged-2c83ca7-smoke-20261021.trx`).

## Services on, fresh profile

A one-off probe launched the packaged executable on the second monitor with a new isolated profile and `AutoManageServices=true`. The qBittorrent host was set to `qbittorrent.invalid`, so no local client or port could be touched; none is installed here. Evidence is in `services-on-2c83ca7-20261021/services-on-result.json`, with the profile and log beside it.

- The main window appeared in **0.88 s**.
- Python preparation deployed `scraper.py`, `book_scraper.py` and `audiovisual_scraper.py` into the profile's `Local\UniversalMediaOS\Services`, each **byte-identical** to the package. The scraper reported ready about **1.9 s** after launch, with no package install needed on this machine.
- The pinned **uBlock Origin 1.71.0** was downloaded from its GitHub release, checked against the pinned SHA-256 and installed into the profile ("Verified uBlock Origin 1.71.0 installed").
- FFmpeg was detected on PATH. qBittorrent was not found, so torrent playback relies on its WebUI or the OS handler.
- The final health line: **`RequiredOk=True; Scraper=True; HLS=True; uBlock=True; FFmpeg=True; qBit=False`**.
- A normal close through the window took **0.12 s** and logged a full progress drain. Afterwards `--verify` found the package **unchanged**.

**Observation, not changed:** the health line is logged about 13 times within those 2 s, once per dependency step. It is harmless log noise.

## Limits

- This machine already had the pinned Python packages, so a pip install on a clean machine was not exercised; earlier candidates covered Repair.
- The hosted checkpoint for 5389c9a..2c83ca7 still waits for the push.
