# 🕵️ QA E2E Black-Box Test & Codebase Audit Report

**Date**: June 19, 2026  
**Target Application**: Universal Media OS (WPF Desktop App)  
**Role**: Senior QA Engineer  

---

## Executive Summary
A comprehensive end-to-end black-box test and architecture audit was performed on the Windows WPF desktop application `UniversalMediaOS`. While the core functionality (UI tab transitions, basic layout, and dependency structure) is in place, several critical and high-severity bugs were uncovered at runtime:
1. **Broken Playback Resume**: The newly refactored MVVM playback page completely bypasses the SQLite database context, rendering progress saving and playback resumption non-functional.
2. **HWND Airspace Scaling Issues**: The dynamic UI scale setting triggers WPF layout transformation scaling, but native HWND hosts (VLC and WebView2) do not respect it, causing layout clipping and overlap.
3. **E2E Test Mismatch**: The entire automated UI test suite is blocked from executing (73/89 failures) due to a simple string title mismatch (`UniversalMediaOS` vs. `Universal Media OS`).
4. **Thread Synchronization/Lifecycle Risks**: High-risk unawaited dispatcher calls and potential race conditions in tab-close VLC player cleanup pose runtime crash and freeze risks.

---

## Test Environment
- **Operating System**: Windows 10 (Build 26200, win-x64)
- **Runtime Environment**: .NET SDK 9.0.310 (Host Runtime: 9.0.12)
- **Target Assembly Configuration**: Debug / AnyCPU (net9.0-windows10.0.17763.0)
- **Version Control**: Git commit `715629d29578e31ed90a5642895d8fcb61ca2dfb` (branch `main`)
- **Launch Command**: `dotnet build UniversalMediaOS.sln` / Run `UniversalMediaOS.WPF.exe`

---

## Coverage Map of Tested Screens & Features
Below is the status of each tested screen and core user flow:

| Component / Flow | Test Type | Status | Remarks |
| :--- | :--- | :--- | :--- |
| **Startup / Fresh Launch** | Automated & Manual | **PASS** | Process launches (~194MB memory usage), applies Mica theme, and initializes dependencies. |
| **Tab Navigation (Sidebar)** | Manual (UI Automation) | **PASS** | Sidebar buttons load "Search", "Library", "Movies", "Books", etc. |
| **Anime Search (SearchView)** | Manual (UI Automation) | **FAIL** | Search triggers scraper query but fails to render list items if the Mock API server is offline. |
| **Manga Search (MangaView)** | Manual (UI Automation) | **PASS** | Search and layout transitions load successfully. |
| **Media Details (AnimeDetailsView)** | Manual (UI Automation) | **WARNING** | Ep inputs validate by overwriting user text, causing bad UX. |
| **Source Selection (Modal)** | Manual (UI Automation) | **PASS** | Modal window pops up correctly on "Watch Now" trigger. |
| **Playback Screen (VLC / WebView)** | Manual (UI Automation) | **FAIL** | Local database saving/resumption is missing; native controls do not scale. |
| **Downloads View** | Manual (UI Automation) | **PASS** | Renders file scanner listings from downloads folder. |
| **Settings Screen** | Manual (UI Automation) | **PASS** | Loads/saves configurations, toggles light/dark themes and density. |
| **App Close / Reopen** | Manual | **PASS** | Graceful shutdown, releases player and bootstrapper processes. |

---

## Detailed Bug Table

| Bug ID | Title | Severity | Category | Likely Affected Area |
| :--- | :--- | :--- | :--- | :--- |
| **BUG-01** | Playback Resume State is Completely Broken / Bypassed | **Critical** | Playback / Data | [PlaybackViewModel.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs) |
| **BUG-02** | WPF Native HWND Airspace scaling clips player rendering | **High** | UI/UX / Layout | [PlaybackView.xaml](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml) |
| **BUG-03** | E2E Test Suite Automation fails due to window title mismatch | **High** | Test Infra | [AppFixture.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/Infrastructure/AppFixture.cs) |
| **BUG-04** | Native Crash Risk on Playback Unload due to async dispatcher | **Medium** | Crash / Lifecycle | [PlaybackView.xaml.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs) |
| **BUG-05** | Confusing UX: Silently overwriting invalid episode inputs | **Medium** | UI/UX | [AnimeDetailsViewModel.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AnimeDetailsViewModel.cs) |
| **BUG-06** | Unawaited Dispatcher Tasks in App & PlaybackTheater | **Medium** | Logic / Threading | [App.xaml.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml.cs) |
| **BUG-07** | Missing Await Operators in async timers (warnings CS1998) | **Low** | Logic / Perf | [PlaybackTheater.xaml.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/PlaybackTheater.xaml.cs) |
| **BUG-08** | Redundant scraper.py copy operations on each boot | **Low** | Performance | [PythonBootstrapper.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.Core/Services/PythonBootstrapper.cs) |

---

## Detailed Bug Writeups

### BUG-01: Playback Resume State is Completely Broken / Bypassed
- **Severity**: Critical
- **Category**: Playback / Data
- **Repro Steps**:
  1. Go to Anime Search tab, search and load details for any show.
  2. Click "⚡ Watch Now" and play an episode.
  3. Pause the video at `02:00` and close the playback tab.
  4. Re-open the same episode and click watch.
- **Expected Result**: Playback should load the saved position from SQLite (`DatabaseContext`) and resume at `02:00`.
- **Actual Result**: Playback always starts at `00:00`.
- **Root Cause & Affected Area**: The refactored [PlaybackViewModel.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs) completely omits references to `DatabaseContext` or `ResumeState`. It fails to query local progress on startup and fails to write position updates to SQLite on tick. (Note: Only the old `PlaybackTheater.xaml.cs` utilized `db.SaveResumeState()`).
- **Confidence**: 100%

### BUG-02: WPF Native HWND Airspace Scaling Clips Player Rendering
- **Severity**: High
- **Category**: UI/UX / Layout
- **Repro Steps**:
  1. Open Settings -> Appearance.
  2. Adjust `UiScale` (e.g. increase to 125% or decrease to 90%).
  3. Open a video or web stream in the Playback view.
  4. Resize the window or enter fullscreen mode.
- **Expected Result**: The player panel and control bars scale uniformly with the rest of the application.
- **Actual Result**: The video player (`vlc:VideoView`) and browser (`wv2:WebView2CompositionControl`) host native Win32 controls (HWNDs). In WPF, layout transforms (such as `ScaleTransform` bound to `UiScale` on the root Grid) do not apply to native HWND hosts. This causes the player area to render offset, clipped, or overflow outside of the styled borders.
- **Root Cause & Affected Area**: Layout transforms do not scale HWNDs. The root layout grid in [MainWindow.xaml](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml) applies a ScaleTransform that breaks native airspace boundaries.
- **Confidence**: 100%

### BUG-03: E2E Test Suite Automation Mismatch (73 test failures)
- **Severity**: High
- **Category**: Test Infrastructure
- **Repro Steps**:
  1. Execute `./run-e2e-tests.ps1` in PowerShell.
- **Expected Result**: The automation test suite runs and completes with all UI automation tests passing.
- **Actual Result**: 73 of 89 tests fail with `Assert.NotNull() Failure: Value is null` at `NavigateTo(String tabName)`.
- **Root Cause & Affected Area**: In [AppFixture.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/Infrastructure/AppFixture.cs) (line 133), the test suite queries top-level windows for `w.Title == "UniversalMediaOS"` (no spaces). However, the WPF main window title in [MainWindow.xaml](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml) (line 4) is `"Universal Media OS"` (with spaces). Due to this mismatch, `AppFixture` fails to locate the main window, falls back to a hidden utility window with 0 descendants, and fails all subsequent button navigation lookups.
- **Confidence**: 100%

### BUG-04: Native Crash Risk on Playback Unload (VlcPlayer Detach)
- **Severity**: Medium
- **Category**: Crash / Lifecycle
- **Repro Steps**:
  1. Open a video file to play.
  2. Double-click the tab close button `[x]` to immediately close the playback screen.
- **Expected Result**: The tab closes cleanly and media resources are released.
- **Actual Result**: The application may crash or log native exceptions.
- **Root Cause & Affected Area**: When a tab is closed, `MainViewModel.CloseTab()` disposes the `PlaybackViewModel` first, releasing `MediaPlayer` and native LibVLC instances. However, the WPF `UserControl`'s `Unloaded` event fires asynchronously afterwards and calls `VlcPlayer.MediaPlayer = null` on the already-disposed instance inside [PlaybackView.xaml.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs) (line 218), leading to native Access Violations.
- **Confidence**: High

### BUG-05: Confusing UX: Silently Overwriting Invalid Episode Inputs
- **Severity**: Medium
- **Category**: UI/UX
- **Repro Steps**:
  1. Open the details page for any anime.
  2. Focus the episode textbox, and type an invalid value, e.g. `-1` or `abc`.
- **Expected Result**: The field highlights in red (validation error) or shows a warning message without mutating the user's active keyboard typing.
- **Actual Result**: The input is instantly overwritten with `"1"` programmatically, throwing off user focus.
- **Root Cause & Affected Area**: In [AnimeDetailsViewModel.cs](file:///c:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AnimeDetailsViewModel.cs) (line 384), `EnsureSelectedEpisodeIsAvailable()` forces invalid inputs back to `"1"` or `"0"` during property change notification, which triggers a recursive text update in the WPF binding instead of standard WPF validation.
- **Confidence**: 100%

### BUG-06: Unawaited Dispatcher Tasks in App & PlaybackTheater
- **Severity**: Medium
- **Category**: Logic / Threading
- **Repro Steps**:
  1. Compile the application in MSBuild/dotnet CLI.
- **Expected Result**: Clean compilation without thread synchronization warnings.
- **Actual Result**: MSBuild outputs warning `CS4014` pointing to unawaited async tasks in `App.xaml.cs` (line 98) and `PlaybackTheater.xaml.cs` (line 394).
- **Root Cause & Affected Area**: Dispatcher operations are invoked asynchronously without being awaited or structured to capture potential background exceptions, risking silent errors.
- **Confidence**: 100%

---

## Performance & Memory Observations
- **Initial Memory Footprint**: ~190-200MB.
- **Service Initialization**: The app runs pip verification on boot, which is fast when packages are cached (~50ms) but can freeze UI initialization by ~2-3 seconds if pip verification has thread locks or network lookup delays.
- **Virtualization**: The `ResultsScrollViewer` in SearchView is virtualization-enabled. However, rapid tab-switching back and forth between active playback and large search pages causes UI thread stutters due to visual tree reconstructions.

---

## UX & Polish Observations
- **Text overwriting**: Programmatic correction of textboxes on property changes causes a poor typing experience.
- **No close feedback**: Closing a playing video has no validation prompt, meaning users might accidentally lose their position.
- **Missing offline states**: When the scraper microservice fails, the user is presented with a generic "No anime found" message instead of a clear connection failure warning.
---

## Things That Worked Correctly
During E2E testing, several modules and core features performed flawlessly and met all design specifications:
- **Clean Application Startup & Bootstrapping**: The app starts up successfully in under 2 seconds, initializing core service configurations and logging initialization steps properly.
- **Sidebar Tab Layout & Navigation**: Manual navigation and tab transitions (Search, Library, Movies, Books, Manga, Downloads, Settings) occur smoothly without UI lockups or freezing.
- **Local Downloads File Scanner**: The scanner correctly crawls the downloads directory and identifies existing video file paths, populating the Downloads view instantly.
- **Configuration Management**: Custom settings (such as light/dark mode theme choices, torrent preferences) load from and write to the SQLite and JSON configurations correctly.
- **VLC Video Player & WebView Switching**: The application successfully switches between the VLC player for video files and the WebView2 interface for web sources, launching native processes cleanly.

---

## Untested Areas & Justifications
- **Multi-Monitor Layouts**: DPI scaling and window bounds were not physically tested across high-DPI setups (e.g. 4K displays at 150% scaling) due to test runner sandboxing.
- **Native Cast (Chromecast)**: Detached device casting could not be verified because no physical casting devices were available in the test runner's subnet.
- **Real Torrent Downloads**: Magnet file piece picking could only be verified against the mock HTTP qBittorrent wrapper; true P2P swarm connection speeds were not tested.

---

## Prioritized Fix Order

1. **BUG-03 (E2E Test Mismatch)**: Fix the title string in the E2E test fixture to allow automated testing to run correctly.
2. **BUG-01 (Broken Playback Resume)**: Re-integrate database state saving and loading in the new `PlaybackViewModel`.
3. **BUG-02 (HWND Scaling issues)**: Restructure the playback layout grid or avoid scaling HWND controls with WPF layout transforms.
4. **BUG-04 (Native Crash Risk)**: Fix the order of cleanup in `PlaybackView_Unloaded` to ensure the VLC control detaches before disposing the ViewModel.
5. **BUG-05 (Episode input UX)**: Implement proper WPF validation rules or text masking instead of silent programmatic overwrites.
6. **BUG-06 & BUG-07**: Clean up thread warnings (CS4014 and CS1998).
