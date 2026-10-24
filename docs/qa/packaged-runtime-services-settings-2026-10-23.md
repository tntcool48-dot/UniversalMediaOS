# Packaged runtime, services and Settings action states — October 23–24, 2026

Part of the open "Qualify remaining packaged runtime/CLI compatibility, service startup/shutdown and Settings accessibility/action states" item. Branch **claude/packaged-runtime-f0b706**, cut from **e744dcd** (the green f0937c4 checkpoint plus its record). Every app run used its own AppFixture or probe profile (`UNIVERSAL_MEDIA_OS_DATA_ROOT`) under the shared desktop lock; the user's profile, %APPDATA% data and downloads were not touched. Evidence (TRX, probe JSON, logs) is in `.artifacts/implementation/packaged-runtime-20261023/` of the main checkout.

Four defects were found and repaired, each with a failing case first: an interpreter too old for the pins was accepted, busy Settings actions dropped keyboard focus, an empty list was an unnamed Tab stop, and dub-provider rows were announced ambiguously with Delete dropping focus. The service-failure journeys found no defect. By the user's decisions of October 24, the app now runs on a newer .NET major when .NET 9 is missing, and the low-memory placeholder is excluded.

## Package under test

A framework-dependent Release `dotnet publish` of **8ab223f** in this worktree, recorded by `tools/write-release-manifest.py`: **1,362 files / 417,684,903 bytes**, the same 7 application / 40 managed / 968 native / 3 script / 344 asset split as candidate f0937c4, 194 source inputs, deployed scripts matching their source. It is a qualification build, not a release candidate; the candidate folder and final manifest were not touched.

The candidate's 9 packaged journeys (six startup/navigation, close past the drain deadline, pressured source admission, second launch on the same profile) and the 5 new real-app cases below ran against this package's executable:

| Runtime | Result | App processes observed |
| --- | --- | --- |
| .NET 9.0.12 (as published) | **14 / 14**, 2 min 36 s (`packaged-8ab223f-net9.trx`) | 10, all on `Microsoft.NETCore.App\9.0.12` |
| .NET 10.0.11 (`DOTNET_ROLL_FORWARD=LatestMajor` for the app only) | **14 / 14**, 2 min 35 s (`packaged-8ab223f-net10.trx`) | 10, all on `Microsoft.NETCore.App\10.0.11` |

Afterwards `--verify` reported the package unchanged.

After the roll-forward change (section 3) the same was repeated on **868d088** (1,362 files / 417,684,944 bytes; its identity now reads "roll-forward Major"):

| Machine | Result | App processes observed |
| --- | --- | --- |
| .NET 9 installed (this machine) | **14 / 14**, 2 min 38 s (`packaged-868d088-net9.trx`) | 10, all on `Microsoft.NETCore.App\9.0.12` |
| Only .NET 10 (`DOTNET_ROOT` of the app pointed at a private folder holding only 10.0.11; no roll-forward override) | **13 / 14**, 2 min 56 s (`packaged-868d088-net10.trx`); then the failed case **3 / 3** | 10, all on the private `10.0.11` |

The one failure was the dub-provider journey: its URL field held `nhttps://…`, a stray "n" keystroke, so the app rightly showed "Validation Error". The same case then passed three times on each runtime; the stray key's source is not established.

## 1. Python runtime compatibility

**Interpreter matrix.** Python 3.10.6, 3.12.10 and 3.13.2 (installed here) each got a fresh venv with the five pins; pip installed them on all three. In each venv:

- the Python suite (`tools/tests`, 229 tests) passes on 3.12 and 3.13. On 3.10 the **225 scraper tests pass**; the 3 release-manifest tool tests error on `hashlib.file_digest` (3.11+), a build-machine tool that is not shipped;
- all three deployed scripts load their whole import graph (434–478 modules) and answer their entry point;
- curl_cffi's bundled libcurl (impersonating Chrome), httpx, lxml and BeautifulSoup fetch and parse a UTF-8 page from a loopback server, with identical non-Latin text.

**Defect: an interpreter too old for the pins was accepted.** `curl_cffi 0.15.0` and several of its dependencies (anyio, cffi, requests, tldextract, websocket-client) declare `Requires-Python >=3.10`, but preparation took the first candidate answering "Python 3.". With a 3.9 first on PATH, every pip install failed even when `py.exe` offered 3.12, and Settings said only `pip_failed`. Preparation now skips 3.x interpreters older than 3.10 and tries the next candidate. When only older ones exist it reports **`python_too_old`** without installing; Python 2 still reports `python_missing`.

| Run | Result |
| --- | --- |
| New cases against the original `PythonBootstrapper` | **3 failed / 25 passed** (`python-version-gate-before.trx`) |
| Preparation class plus the missing-script case, repaired | **29 / 29** (`python-version-gate-after.trx`) |

No Python older than 3.10 is installed here, so the gate is proved with the fake runner only.

Each preparation state change is now logged (`[Python] Preparation Failed (pip_failed): curl_cffi, …`), so a support log says why Python is not ready instead of only `Scraper=False`.

## 2. The packaged Python service on clean interpreters

`tools/qualify-python-runtime.py` starts the packaged executable in a profile of its own with services on, PATH limited to one interpreter's folder and Windows' own folders, the pinned uBlock manifest already present (no extension download) and qBittorrent pointed at an invalid host. It reads the `[Python] Preparation` log lines, closes the window as its title-bar button does, and looks for interpreter processes left running. Each interpreter was a fresh venv with no packages, so the app's own preparation had to install the pins with that venv's pip.

| Interpreter (bundled pip) | Package index | Outcome | Close |
| --- | --- | --- | --- |
| 3.10.6 (pip 22.2.1) | PyPI | all five imports missing → **Ready at 15.6 s**; exact pins installed | normal, 0.11 s |
| 3.12.10 (pip 25.0.1) | PyPI | **Ready at 15.8 s**; exact pins installed | normal, 0.11 s |
| 3.13.2 (pip 24.3.1) | PyPI | **Ready at 14.5 s**; exact pins installed | normal, 0.09 s |
| 3.12.10 | loopback port with nothing listening | **Failed (pip_failed) at 23.6 s**, listing the five missing imports | normal, 0.11 s |
| 3.13.2 | loopback port that accepts and never answers | still Preparing at 25 s with `pip install` running; window closed then | normal, **0.11 s**; no interpreter process left 1 s later |

In every run the three deployed scripts were byte-identical to the package. The close during a stalled install drained progress and exited without waiting for pip, and pip did not outlive the app in this run.

## 3. .NET runtime

The package needed the .NET 9 Desktop runtime; by default it rolled forward only to newer 9.x patches.

- **Before:** against a private runtime folder holding only .NET **10.0.11**, packaged 8ab223f exited with code **150**: "You must install or update .NET to run this application … Framework 'Microsoft.NETCore.App', version '9.0.0'". Users see that as a dialog. Nothing in the profile was created. With roll-forward forced through `DOTNET_ROLL_FORWARD=LatestMajor` it ran there: the window in 0.78 s, a normal close in 0.14 s, and the 14 journeys above.
- **User decision (October 24): allow a newer .NET.** The WPF project now sets `<RollForward>Major</RollForward>`: .NET 9 wherever it is installed, otherwise the lowest newer major. `LatestMajor` was not chosen because it would move every machine that also has .NET 10 onto 10 even with 9 present. The published `UniversalMediaOS.WPF.runtimeconfig.json` carries `"rollForward": "Major"`, and `tools/write-release-manifest.py` now records the policy in each build identity (its test passes; Python suite **230 / 230**).
- **After:** packaged 868d088 against the same .NET-10-only folder, with no override: window in **0.77 s**, coreclr and WPF loaded from the private 10.0.11, normal close in 0.14 s with exit code 0 and the progress drain logged. The journeys are in the second table above; on this machine, which has .NET 9, every app process still ran on 9.0.12.

Only .NET 10 was tested. Future majors (11 and later) would also be used on a machine without .NET 9 and are untested.

## 4. Services failing or slow at startup, in the real app

`ServicesOn_MissingToolsAndAStalledExtensionDownloadAreReportedWithoutBlockingTheApp` starts the app with services on and only Windows' own folders on PATH, so FFmpeg and ffprobe are missing and no Python is found. A loopback proxy (`HTTPS_PROXY`) accepts the uBlock download's connection and never answers, like a stalled network; nothing leaves the machine.

- While the download hangs, FFmpeg's result is already published ("ffmpeg missing; ffprobe missing."), the status bar says "Preparing services...", and Settings still switches sections.
- After the download's 20-second limit the uBlock line reads "Timed out after 20 seconds during uBlock Origin download." and the status bar "Service attention needed". Python reads "Failed (python_missing)", never Ready.
- Repair services shows its busy state, retries the download (a new proxy connection) and ends in the same honest states; no extension folder is left behind.

It passes in **46 s** (`services-on.trx`), and again on both runtimes above. No app defect was found.

## 5. Settings action states and keyboard

**Defect: busy actions dropped keyboard focus.** Repair services and Save Settings disable themselves while they run. WPF then moved keyboard focus to an unnamed container: the section's ScrollViewer after Repair, the "View" pane after Save's confirmation. A screen reader announces just "pane", and Tab restarts at the top of the section. `KeyboardFocusReturn.AfterDisabled`, set once on the Settings view, returns focus to the control once it is available again, unless the user moved focus elsewhere meanwhile.

**Defect: an unnamed, empty Tab stop.** A new audit tabs through all eight sections from each section button and requires every visible control to be reached once, in layout order, with no unnamed stop. It found the dub-provider list (an `ItemsControl`, focusable by default) in Providers & Scrapers, announced as "list". The container is no longer a Tab stop (its rows' controls are) and is named "Dub availability providers".

**Defect: rows revealed by Add Dub Provider were ambiguous, and Delete dropped focus.** Each provider row's list item was announced as the class name `UniversalMediaOS.WPF.ViewModels.DubAvailabilityProviderSettingsItem`, its check box as just "Enabled" and its button as just "Delete", identical for every provider. Deleting from the keyboard removed the focused button and left focus nowhere. The item now reads as the provider, the controls as "<provider> enabled" and "Delete <provider>", and Delete moves focus to the row that takes its place, or to the "Dub provider name" field when none is left.

| Case (real app, keyboard) | Before | After |
| --- | --- | --- |
| `Settings_RepairServicesKeepsKeyboardFocusThroughItsBusyState`: Enter on Repair → disabled while preparing → enabled with focus → Tab reaches Save | **Fail**: focus on a `ScrollViewer` pane | Pass, 5 s |
| `Settings_SaveKeepsKeyboardFocusAfterItsConfirmation`: Enter on Save → Enter on "Settings Saved" → focus on Save | **Fail**: focus on a `View` pane | Pass |
| `Settings_TabReachesEverySectionControlInOrderWithoutUnnamedStops` | **Fail**: unnamed `List` stop | Pass |
| `Settings_AddedDubProviderRowsAreNamedAndDeleteKeepsKeyboardPlace`: add two providers → named rows → Delete → focus on the next row → Delete → focus on the add form | **Fail**: "Enabled" / "Delete"; once those were named, the item still read as the class name | Pass, 17 s |
| Whole `SettingsTests` class, after all repairs | — | **15 passed, 1 skipped** (the low-memory placeholder), 2 min 31 s |

UI Automation's `FindAll` from the list element returns none of its rows while tree navigation does; Narrator navigates, so this is noted, not changed.

## 6. FFmpeg, qBittorrent, WebView2

- **FFmpeg:** CI runs FFmpeg 7.1.1; this machine has 8.0. Here the real-media download class and the dependency-health cases pass **47 / 47** (`ffmpeg-8.0-real-media.trx`), including a real encode → DASH download → remux → ffprobe languages → full decode.
- **qBittorrent:** none is installed here. Its Web API (5.x session cookie, add response, pause/resume) was qualified with a private 5.2.4 on October 15. Automatic start (`qbittorrent.exe --webui-port=<port>`) only runs for a copy in Program Files or `%LOCALAPPDATA%\qBittorrent`, real locations outside an isolated profile, so it was not exercised. Portable or Scoop installs are not detected; Settings then shows qBittorrent as optional and still uses the configured WebUI.
- **WebView2/uBlock:** uBlock 1.71.0 loaded enabled in WebView2 runtime 155.0.4283.45 on October 11. This machine now has patch builds .60/.62 of the same 155 runtime; no browser journey was re-run here (website journeys belong to other items).

## 7. Low-memory Settings placeholder: excluded by the user

The user accepted the proposed exclusion on **October 24, 2026**. `T2_Settings_04_Low_Memory_Resource_Boundary` stays skipped and is never counted. The behavior it names no longer exists: recovery commit **182da1b** removed the startup RAM check (`SystemResourceCheck.PerformStartupCheck`) and Settings' "Current Resource load" line. `SystemResourceCheck` is now used only by its own unit tests; no app code reads available memory or reacts to it. A real low-memory condition (for example a Job Object memory cap on the app process) would test .NET out-of-memory failure in whatever code allocates first, not a Settings behavior, and would not repeat reliably. Save's safety under failure (a failed replace leaves `config.json` byte-identical) is already covered by `T2_Settings_03_Settings_Lock_Collision`. Bringing a memory check back would be a product decision. The skip reason and the [skip inventory](skipped-test-inventory-2026-10-05.md) now say this.

## What remains open

- **Narrator speech (waits on the user).** UI Automation names, focus and Tab order are verified; what Narrator actually says is not. `.artifacts/packaged-runtime/narrator-check/Start-NarratorCheck.ps1` in the worktree starts the packaged 8ab223f app in a fresh isolated profile.
- **Real older or newer interpreters.** A real Python 3.9 (`python_too_old`) and 3.14 were not run; each needs a download.
- **Product decisions:** detecting qBittorrent outside its two installer locations; bringing back a memory check. (.NET roll-forward and the low-memory exclusion were decided on October 24.)
- **Future .NET majors** (11 and later) are untested; only 10.0.11 ran.
- **Services stopping during use.** Startup failure and slowness are covered for Python, FFmpeg and uBlock. By code inspection, an interpreter removed while the app runs keeps a cached Ready until Repair, and Repair does not restart an HLS proxy that failed to start; neither could be triggered realistically here. qBittorrent going offline during a download belongs to the Downloads placeholder.
- **Arabic/RTL focus and speech** in Settings were not exercised.

## Notes for the release gate

- `tools/write-release-manifest.py` now names "Python 3.10 or newer" (it said 3.12) and ".NET 9 Windows Desktop runtime (a newer major when 9 is missing)", and writes the roll-forward policy into each identity.
- `run-e2e-tests.ps1` stops at the first line dotnet writes to stderr when its output is redirected inside Windows PowerShell 5.1 (`$ErrorActionPreference = "Stop"`); run it through `powershell -File … > log 2>&1` from another shell.
