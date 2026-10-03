# 🕵️ Source-Code White-Box Audit Report

**Date**: June 19, 2026  
**Target Application**: Universal Media OS (WPF Desktop App)  
**Role**: Senior Software Architect / Security Auditor  

---

## Executive Summary

A comprehensive source-code white-box audit was performed across the `UniversalMediaOS` solution. By combining the findings of the previous black-box QA run with a deep code review, we confirmed the underlying logic causing all observed runtime errors. Additionally, several critical and high-priority architectural, concurrency, performance, and dead-code issues were uncovered:

1. **Stale Results & Memory Leaks via Unawaited Tasks**: Transient viewmodel constructors in [SearchViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs) and [MangaViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MangaViewModel.cs) trigger un-cancellable async calls (`CancellationToken.None`), allowing obsolete tasks to continue executing and leak references.
2. **Sequential Network Requests Stalling Search**: When filtering results by Dub, the search view queries the dub checker sequentially in a loop, stalling search load times by up to 10–18 seconds.
3. **WPF UI virtualization is completely disabled**: Grid results in `SearchView.xaml` and pages in `MangaView.xaml` are rendered in non-virtualizing layout controls inside scroll views, generating massive rendering overhead and memory leaks as items accumulate.
4. **Offline Boot Hang**: Startup tries to download uBlock Origin from GitHub without a timeout limit, hanging background service initialization for up to 100 seconds when offline.
5. **StaticResource bindings break themes**: Core background, foreground, and border brushes use `StaticResource` instead of `DynamicResource`, preventing real-time theme/density changes from updating until app restart.

---

## Architecture & Project Structure Assessment

* **MVVM Correctness**: The dynamic view mapping via `DataTemplate` in [App.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml) is highly clean and correct. However, some lifecycle events in code-behinds (such as [PlaybackView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs)) access ViewModel internals directly, leading to teardown race conditions and native access violations.
* **Dependency Flow**: Correctly structures Core/WPF boundaries, but database state updates are entirely missing in the MVVM playback layer.
* **Dead Code**: Obsolete WPF elements (`LegacyMainWindow`, `PlaybackTheater`, `EpubReaderService`, and the entire `Casting/HybridSourceMatcher` directory) inflate codebase size and confuse service dependency registration.

---

## Confirmed Causes of Runtime Bugs

Below is the code-level confirmation of the bugs documented in the black-box QA pass:

### BUG-01: Broken Playback Resume State
* **Severity**: Critical
* **File Reference**: [PlaybackViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs)
* **Line Number**: 13 (Class definition)
* **Code Excerpt / Description**: The class contains no database injection or SQL context handlers. Progress tracking ticks only update the local memory state and MAL API, while SQLite table updates are completely omitted.
* **Why it is a problem**: The feature was never ported to MVVM. Playback position is never saved, and media always starts at `00:00` when loaded.
* **Runtime Symptom**: Progress saving and resumption are completely non-functional.
* **Recommended Fix**: Inject `DatabaseContext` into [PlaybackViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs), load saved position on startup, seek the player, and write updates on tick/time changes using `SaveResumeStateAsync`.
* **Risk of Fixing**: Low.

### BUG-02: WPF Native HWND Airspace scaling clips player rendering
* **Severity**: High
* **File Reference**: [MainWindow.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml#L114)
* **Line Number**: 114
* **Code Excerpt / Description**:
  ```xml
  <Grid.LayoutTransform>
      <ScaleTransform ScaleX="{Binding SettingsViewModel.UiScale}" ScaleY="{Binding SettingsViewModel.UiScale}"/>
  </Grid.LayoutTransform>
  ```
* **Why it is a problem**: Win32 HWND hosts (VLC's `VideoView` and WebView2 controls) do not respect layout transforms, leading to composition scale boundary displacement.
* **Runtime Symptom**: Adjusting UI scale in settings causes the video player or embedded web view to render at the wrong size, offset, or clipped.
* **Recommended Fix**: Avoid scaling native container zones in [MainWindow.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml) via a root layout `ScaleTransform`. Keep the player grid unscaled or manually handle its layout/sizing.
* **Risk of Fixing**: Medium. Modifying the window-wide scale transform requires checking alignment and sizing of all non-player views (like settings card margins and details text wrapping).

### BUG-03: E2E Test Mismatch
* **Severity**: High
* **File Reference**: [AppFixture.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/Infrastructure/AppFixture.cs#L133)
* **Line Number**: 133
* **Code Excerpt**: `var mainWin = System.Linq.Enumerable.FirstOrDefault(windows, w => w.Title == "UniversalMediaOS");`
* **Why it is a problem**: The actual main window title defined in [MainWindow.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml#L4) is `"Universal Media OS"` (with spaces).
* **Runtime Symptom**: E2E tests cannot find the window and fail with `Assert.NotNull() Failure: Value is null` when trying to locate sidebar navigation buttons.
* **Recommended Fix**: Correct the title query string in [AppFixture.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/Infrastructure/AppFixture.cs#L133) to `"Universal Media OS"`.
* **Risk of Fixing**: Low.

### BUG-04: Native Crash Risk on Playback Unload (VlcPlayer Detach)
* **Severity**: High
* **File Reference**: [MainViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MainViewModel.cs#L207) & [PlaybackView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs#L218)
* **Line Number**: [MainViewModel.cs:207-210](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MainViewModel.cs#L207-L210) & [PlaybackView.xaml.cs:218](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs#L218)
* **Code Excerpt**: `playback.StopAndRelease(); playback.Dispose();` in ViewModel happens before the tab is removed. When the view's `Unloaded` event fires subsequently, it calls `VlcPlayer.MediaPlayer = null;`.
* **Why it is a problem**: `VlcPlayer.MediaPlayer = null` tries to access native wrapper hooks on the already-disposed player and libvlc instances, which can trigger access violations in the unmanaged C code, crashing the WPF process.
* **Runtime Symptom**: Random WPF app crashes or exit code `0xC0000005` (Access Violation) when closing a playback tab.
* **Recommended Fix**: Change the cleanup sequence: remove the tab from `Tabs` first (triggering the View's `Unloaded` and detaching the `MediaPlayer`), and *then* dispose the viewmodel resources safely.
* **Risk of Fixing**: Medium.

### BUG-05: Confusing UX: Silently Overwriting Invalid Episode Inputs
* **Severity**: Low
* **File Reference**: [AnimeDetailsViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AnimeDetailsViewModel.cs#L384)
* **Line Number**: 384 (inside `EnsureSelectedEpisodeIsAvailable`)
* **Code Excerpt / Description**: Mutating binding source strings inside property change handlers causes recursive text updates that override active typing.
* **Why it is a problem**: However, since the refactored details view has no textboxes and only buttons, this only affected the legacy, obsolete main window.
* **Runtime Symptom**: In the obsolete legacy view, typing invalid characters in the episode input instantly overwrites them, messing up user cursor focus.
* **Recommended Fix**: Keep as is for the refactored view, but clean up the obsolete `LegacyMainWindow.xaml` code.
* **Risk of Fixing**: None (since it's dead code).

### BUG-06: Unawaited Dispatcher Tasks in App Startup
* **Severity**: Medium
* **File Reference**: [App.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml.cs#L66)
* **Line Number**: 66
* **Code Excerpt**: `_ = InitServicesAsync();`
* **Why it is a problem**: Fires a task on the thread pool without awaiting it, causing compiler warning CS4014. Background exceptions can go unhandled.
* **Runtime Symptom**: Startup tasks run asynchronously but are silent if they fail, though exceptions are logged.
* **Recommended Fix**: Await or handle the task safely, capturing any exceptions.
* **Risk of Fixing**: Low.

### BUG-07: Missing Await Operators in async timers (warnings CS1998)
* **Severity**: Low
* **File Reference**: [PlaybackTheater.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/PlaybackTheater.xaml.cs#L428)
* **Line Number**: 428
* **Code Excerpt / Description**: Async methods (e.g. `MalTimer_Tick`) do not contain `await` operators, generating CS1998 compiler warnings.
* **Why it is a problem**: It is inside the obsolete, dead `PlaybackTheater.xaml.cs` file.
* **Runtime Symptom**: None (dead code).
* **Recommended Fix**: Delete the dead code file.
* **Risk of Fixing**: None.

### BUG-08: Redundant scraper.py copy operations on each boot
* **Severity**: Low
* **File Reference**: [PythonBootstrapper.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/PythonBootstrapper.cs#L82)
* **Line Number**: 82
* **Code Excerpt**: `File.Copy(srcPath, destPath, overwrite: true);`
* **Why it is a problem**: Performs a blocking write on every startup, even if the scraper file hasn't changed.
* **Runtime Symptom**: Unnecessary disk write on boot.
* **Recommended Fix**: Check if the file already exists and compare file hashes/timestamps before copying, or run the write asynchronously.
* **Risk of Fixing**: Low.

---

## New Code-Only Issues

### AUDIT-01: Constructor unawaited task memory leaks and stale state overwrites
* **Severity**: High
* **File Path**: [SearchViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs#L87-L88) & [MangaViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MangaViewModel.cs#L53)
* **Line Number**: [SearchViewModel.cs:87-88](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs#L87-L88) & [MangaViewModel.cs:53](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MangaViewModel.cs#L53)
* **Code Excerpt**:
  ```csharp
  _ = LoadTagFiltersAsync(CancellationToken.None);
  _ = LoadRecommendationsAsync(CancellationToken.None);
  ```
* **Why it is a problem**: Background tasks are fired in constructors without any cancellation token or lifecycle tracking. Since these viewmodels are transient, switching tabs instantiates new models while the old ones keep running their tasks in the background, updating collection bindings on discarded viewmodels.
* **Runtime Symptom**: Stale background network queries keep executing, consuming bandwidth and memory, and potentially overwriting UI states.
* **Recommended Fix**: Do not fire async work directly in the constructor. Use a navigation/lifecycle trigger, pass a cancellation token associated with the viewmodel, and cancel it when the viewmodel is disposed.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-02: Sequential network loop checks bottleneck search performance
* **Severity**: High
* **File Path**: [SearchViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs#L332)
* **Line Number**: 332-347 (inside `ApplyAudioFilterAsync`)
* **Code Excerpt**:
  ```csharp
  foreach (var result in results)
  {
      token.ThrowIfCancellationRequested();
      var availability = await _dubAvailabilityService.CheckAsync(result, token);
      ...
  ```
* **Why it is a problem**: Performs sequential network check requests. 36 search results require 36 sequential HTTP queries, stalling search loading by up to 18 seconds.
* **Runtime Symptom**: Search queries freeze in the loading state for a long time when "Dub" filter is active.
* **Recommended Fix**: Query the list concurrently using `Task.WhenAll` with a `SemaphoreSlim` limit (e.g. limit to 5 concurrent connections).
* **Risk of Fixing**: Medium. Ensure we do not trigger too many concurrent requests to the provider to avoid HTTP 429 rate limits.
* **Matches black-box bug**: No.

### AUDIT-03: Scroll list lacks UI virtualization, causing layout thrashing
* **Severity**: Medium
* **File Path**: [SearchView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml#L227) & [MangaView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml#L338)
* **Line Number**: [SearchView.xaml:227](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml#L227) & [MangaView.xaml:338](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml#L338)
* **Code Excerpt**: `ItemsControl` inside a `ScrollViewer` using a `WrapPanel` or StackPanel.
* **Why it is a problem**: Bypasses WPF's UI virtualization. All items in the list (including images) are instantiated in the visual tree, causing high memory usage and layout stutters during scrolling.
* **Runtime Symptom**: High memory usage, visual stutters, and lag when scrolling through large search results or reading manga chapters.
* **Recommended Fix**: Use a `ListBox` or `ListView` and set their `ItemsPanel` to a virtualizing panel (like `VirtualizingWrapPanel` or `VirtualizingStackPanel`) with recycling enabled.
* **Risk of Fixing**: Medium. Changing layout panels can alter visual spacing and sizing, so visual layout must be verified.
* **Matches black-box bug**: No.

### AUDIT-04: Non-cancellable manga chapter and page load requests
* **Severity**: Medium
* **File Path**: [MangaViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MangaViewModel.cs#L132)
* **Line Number**: 132 & 167 (inside `ReadAsync` and `SelectChapterAsync`)
* **Code Excerpt**: Calling `_mangaService.GetChaptersAsync(manga.Id)` without passing a cancellation token.
* **Why it is a problem**: If the user goes back or switches tabs, the request continues running and updates viewmodel properties when completed, causing unexpected view mode changes.
* **Runtime Symptom**: Stale network responses overwriting UI lists and changing view modes when navigating back.
* **Recommended Fix**: Pass a `CancellationToken` linked to the viewmodel lifecycle and cancel it on navigation/disposal.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-05: Redundant sequential processes check slow down startup
* **Severity**: Low
* **File Path**: [PythonBootstrapper.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/PythonBootstrapper.cs#L153)
* **Line Number**: 153-162 (inside `RunPipInstallAsync`)
* **Code Excerpt**: Loop calling `IsPackageImportableAsync(python, requirement.ImportName, token)` sequentially.
* **Why it is a problem**: Spawns 5 separate python subprocesses on every boot to check packages, taking 1.5–3 seconds.
* **Runtime Symptom**: Delayed application startup initialization.
* **Recommended Fix**: Combine package checks into a single subprocess command (e.g. `python -c "import drissionpage, curl_cffi, httpx, bs4, lxml"`).
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-06: StaticResource bindings prevent theme/density changes from updating elements at runtime
* **Severity**: High
* **File Path**: [SearchView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml#L9) (and other views: [DownloadsView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/DownloadsView.xaml#L87), [MangaView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml#L199), [MyListView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MyListView.xaml#L9), [PlaceholderMediaView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaceholderMediaView.xaml#L10), [AnimeDetailsView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/AnimeDetailsView.xaml#L101))
* **Line Number**: Various (e.g. [SearchView.xaml:9](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml#L9))
* **Code Excerpt**: `Background="{StaticResource BgApp}"` or `Background="{StaticResource BgPanel}"`
* **Why it is a problem**: WPF only resolves `StaticResource` bindings once. If theme/density settings change at runtime, elements using `StaticResource` will not update.
* **Runtime Symptom**: Toggling dark/light mode in settings causes a partially broken/mixed theme layout until the tab is recreated or app is restarted.
* **Recommended Fix**: Change all dynamic key bindings (like `BgApp`, `BgPanel`, `BgPanelAlt`, `BgInput`, `BorderSubtle`, `BorderStrong`, `TextPrimary`, etc.) from `StaticResource` to `DynamicResource`.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-07: Memory leaks in MangaView and SearchView
* **Severity**: Medium
* **File Path**: [MangaView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml.cs#L29) & [SearchView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml.cs#L24)
* **Line Number**: [MangaView.xaml.cs:29-30](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml.cs#L29-L30) & [SearchView.xaml.cs:24-26](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml.cs#L24-L26)
* **Code Excerpt**: Subscribing to viewmodel property/collection changes: `newVm.SearchResults.CollectionChanged += SearchResults_CollectionChanged;`
* **Why it is a problem**: Since the views never unsubscribe from the transient viewmodels when unloaded, the viewmodels keep strong delegate references to the views, leaking view instances in memory.
* **Runtime Symptom**: Increased memory usage over time (memory leak) as tabs are opened and closed.
* **Recommended Fix**: Hook the view's `Unloaded` event and unsubscribe from all viewmodel events and collection notifications.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-08: DubAvailabilityService uses hardcoded external domains instead of loading from configuration
* **Severity**: Medium
* **File Path**: [DubAvailabilityService.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/DubAvailabilityService.cs#L60)
* **Line Number**: 60-64
* **Code Excerpt**:
  ```csharp
  string[] urls =
  [
      $"https://hianime.to/ajax/search/suggest?keyword={query}",
      $"https://aniwatchtv.to/ajax/search/suggest?keyword={query}"
  ];
  ```
* **Why it is a problem**: Hardcodes public domains. During testing, they bypass the local mock server redirects and try to query the public internet directly.
* **Runtime Symptom**: E2E tests fail or hang on offline systems when making external HTTP requests.
* **Recommended Fix**: Retrieve base URLs from `DomainHotSwapper` configuration (e.g. adding `HiAnimeUrl` and `AniWatchUrl` config options) and fall back to public domains if missing.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-09: SeasonDownloader uses manual string interpolation for process arguments instead of ArgumentList
* **Severity**: Medium
* **File Path**: [SeasonDownloader.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Archiving/SeasonDownloader.cs#L621)
* **Line Number**: 621
* **Code Excerpt**: `Arguments = $"-v error ... \"{filePath}\"";`
* **Why it is a problem**: Manually interpolating file paths containing double quotes breaks command parsing. Also exposes a potential argument injection vulnerability if filenames contain malicious characters.
* **Runtime Symptom**: ffprobe fails to validate downloaded files if their names contain quotes.
* **Recommended Fix**: Use `ArgumentList` instead of `Arguments` to delegate safe quoting and escaping to the framework.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-10: Image decoding on UI thread
* **Severity**: Medium
* **File Path**: [AsyncImageLoader.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Controls/AsyncImageLoader.cs#L110)
* **Line Number**: 110-120
* **Code Excerpt**: `bitmap.EndInit();` is executed on the calling context, which is the UI thread after copying stream bytes.
* **Why it is a problem**: Decompressing/decoding images is CPU-intensive. Running it on the UI thread blocks drawing, causing frame drops and visual lag.
* **Runtime Symptom**: Visual lag and stutters when lists with many images (like manga pages or cover arts) are loaded.
* **Recommended Fix**: Initialize and decode the `BitmapImage` inside a `Task.Run` background thread, call `bitmap.Freeze()`, and return the frozen object to be assigned on the UI thread.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

### AUDIT-11: Offline boot hang due to uBlock download timeout
* **Severity**: High
* **File Path**: [DependencyBootstrapper.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/DependencyBootstrapper.cs#L327)
* **Line Number**: 327 (inside `EnsureUBlockOriginAsync`)
* **Code Excerpt**: `using var resp = await _httpClient.SendAsync(req);`
* **Why it is a problem**: The `_httpClient` has no timeout configured (defaulting to 100 seconds). On startup, if the system is offline, the task will block for 100 seconds waiting for the GitHub API, freezing the service initialization sequence.
* **Runtime Symptom**: App startup hangs or shows loading spinners for more than 1.5 minutes on fresh launches without internet access.
* **Recommended Fix**: Pass a `CancellationToken` with a short timeout (e.g. 5-10 seconds) or configure a short timeout on the `_httpClient`.
* **Risk of Fixing**: Low.
* **Matches black-box bug**: No.

---

## File-by-File Review

### WPF Project ([UniversalMediaOS.WPF](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF))

* [App.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml):
  - *Correctness*: Clean DataTemplates mappings.
* [App.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml.cs):
  - *Issues*: Unawaited async call on startup `_ = InitServicesAsync()` (BUG-06/CS4014). Obsolete transient registration of `EpubReaderService`.
* [MainWindow.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml):
  - *Issues*: LayoutTransform `ScaleTransform` bound to `UiScale` breaks native HWND airspace rendering (BUG-02).
* [ViewModels/MainViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MainViewModel.cs):
  - *Issues*: Incorrect cleanup disposal ordering in `CloseTab` (BUG-04).
* [ViewModels/PlaybackViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs):
  - *Issues*: Entirely lacks database connection or progress resume updates (BUG-01). Discards Task results `_ = SyncMalProgressAsync()`.
* [ViewModels/SearchViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs):
  - *Issues*: Unawaited constructor tasks (AUDIT-01), sequential network calls in `ApplyAudioFilterAsync` (AUDIT-02), and no cancellation on pagination.
* [ViewModels/MangaViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MangaViewModel.cs):
  - *Issues*: Unawaited constructor task (AUDIT-01), non-cancellable details and page retrieval (AUDIT-04).
* [Views/SearchView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml):
  - *Issues*: Non-virtualizing `ItemsControl`/`WrapPanel` inside scroll view (AUDIT-03). Dynamic resources loaded as `StaticResource` (AUDIT-06).
* [Views/SearchView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml.cs):
  - *Issues*: Memory leak due to missing unsubscription from `SearchResults.CollectionChanged` (AUDIT-07).
* [Views/MangaView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml):
  - *Issues*: Non-virtualizing layout for pages (AUDIT-03). `StaticResource` theme bindings (AUDIT-06).
* [Views/MangaView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml.cs):
  - *Issues*: Leaks subscriber registration by failing to unsubscribe from `Vm_PropertyChanged` when view is unloaded (AUDIT-07).
* [Views/PlaybackView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs):
  - *Issues*: `async void` event handlers that could cause crashes on exceptions. Access violation risk in `PlaybackView_Unloaded` when detaching player (BUG-04).
* [Controls/AsyncImageLoader.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Controls/AsyncImageLoader.cs):
  - *Issues*: Performs image decoding synchronously on the UI thread, causing UI stutters (AUDIT-10).

### Core Project ([UniversalMediaOS.Core](file:///C:/Users/user/animeapp/UniversalMediaOS.Core))

* [Data/DatabaseContext.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Data/DatabaseContext.cs):
  - *Status*: Clean WAL connection setup and thread-safe Save/Get methods, but completely unreferenced in active MVVM workflows.
* [Services/PythonBootstrapper.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/PythonBootstrapper.cs):
  - *Issues*: Performs blocking file writes on boot (BUG-08) and starts five sequential requirement checks (AUDIT-05).
* [Services/DependencyBootstrapper.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/DependencyBootstrapper.cs):
  - *Issues*: Relies on external GitHub URL calls to install uBlock on boot, failing and blocking service boot when offline (AUDIT-11).
* [Services/DubAvailabilityService.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/DubAvailabilityService.cs):
  - *Issues*: Hardcoded domains bypass configuration bounds and try to access the public internet during test runs (AUDIT-08).
* [Archiving/SeasonDownloader.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Archiving/SeasonDownloader.cs):
  - *Issues*: Manually interpolates arguments into process calls, causing failures on paths with quotes (AUDIT-09).

---

## Prioritized Fix List

1. **BUG-03 (High)**: Correct target window title inside [AppFixture.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/Infrastructure/AppFixture.cs#L133) to allow E2E tests to run.
2. **BUG-01 (Critical)**: Inject `DatabaseContext` into [PlaybackViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs) and re-enable resume position saving/loading.
3. **BUG-04 & views cleanup (High)**: Adjust disposal order in [MainViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MainViewModel.cs#L207) and wrap `VlcPlayer.MediaPlayer = null` inside safe checks in [PlaybackView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs).
4. **AUDIT-11 & AUDIT-08 (High)**: Configure HttpClient timeouts in [DependencyBootstrapper.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/DependencyBootstrapper.cs) and load external domains from config in [DubAvailabilityService.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.Core/Services/DubAvailabilityService.cs) to ensure offline E2E test execution.
5. **AUDIT-02 & AUDIT-04 (High)**: Introduce concurrency and cancellation handling for database/network queries in [SearchViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs) and [MangaViewModel.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MangaViewModel.cs).
6. **BUG-02 & AUDIT-03 (High)**: Address airspace scale bounds by restructuring `PlaybackView` grids and wrapping layout virtualization inside [SearchView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml) and [MangaView.xaml](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml).
7. **AUDIT-06 (High)**: Bind dynamic keys (`BgApp`, `BgPanel`, `BgPanelAlt`, `BgInput`, etc.) using `DynamicResource` in all XAML views to fix live theme swapping.
8. **AUDIT-07 (Medium)**: Hook `Unloaded` events in [SearchView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/SearchView.xaml.cs) and [MangaView.xaml.cs](file:///C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/MangaView.xaml.cs) to unsubscribe from VM property/collection changes and avoid memory leaks.
9. **Dead Code Cleanup (Medium)**: Remove `LegacyMainWindow.*`, `PlaybackTheater.*`, `HybridSourceMatcher.cs`, and `EpubReaderService.cs` from compilation.

---

## "Do Not Fix Yet" Notes (Risky Changes)

> [!WARNING]
> **Refactoring ScaleTransform**: Replacing the window-wide `ScaleTransform` bound to `UiScale` in `MainWindow.xaml` requires modifying the layout sizes of all views. If not done carefully, it may cause alignment breaks across settings cards, details summaries, and result margins. Confining this change to player grids is safer.
> 
> **Global HttpClient Timeout**: Changing timeouts inside `DubAvailabilityService` or `DependencyBootstrapper` could lead to service failures on slow networks. Keep conservative thresholds.

---

## Suggested Test Coverage to Add Later

1. **Unit test for Playback Position Saving**: Add tests to verify that `PlaybackViewModel` writes update marks to the SQLite database.
2. **Tab Close Sequence Mock Test**: Add unit tests in E2E simulating rapid VM disposal and check for exceptions inside the view.
3. **Manga Navigation Cancel Verification**: Mock the `MangaService` to verify that task cancellation tokens are respected.
