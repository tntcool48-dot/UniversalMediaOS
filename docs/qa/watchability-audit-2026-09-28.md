# Watchability audit and revised implementation order — September 28, 2026

This investigation covers the user's latest playback requirements and every remaining work-package item in the [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md). It records defects and implementation instructions; it does not report these defects as repaired. Checkbox progress stays in that one checklist.

## Product decisions from the user

- Finish anime playback before accepting Movies, then Television as working. Search results alone do not count as a working media category.
- Support several open player tabs. Switching tabs pauses the outgoing video and keeps it ready; returning must not restart resolution, lose position, or display a white video surface. Resume remains an explicit user action.
- Native playback uses controls over the video, including fullscreen. Mouse movement reveals them; inactivity hides them without changing video size. Fullscreen fills the display without a permanently reserved controls row.
- Site/WebView playback uses the site's controls. Hide the entire duplicate app playback panel in normal, fullscreen and PiP modes. Retain a way to exit presentation modes and perform recovery through the tab/window menu; internal position reporting and resume may continue without visible duplicate controls.
- Investigate temporary downloads from torrent sources such as Nyaa. Retain the requested episode while its player tab is open, including when paused in the background; release and delete temporary files after their last owning tab closes. A default replacement for streaming has not been selected.
- Keep ordinary discovery/playback keyless where possible. Anime source discovery stays automatic through EverythingMoe and The Index, without static provider selection. Movie metadata stays non-TMDB (currently Wikidata); Television uses TVmaze.
- Prioritize Anna's Archive for books. Cartoons come last and primarily mean old Arabic-dubbed anime such as Flona and Heidi. Category removal is still undecided.

## Evidence and limits

The running executable is [direct-ts-build](C:/Users/user/animeapp/.artifacts/implementation/direct-ts-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), process 53784, with the existing isolated real-service profile `.artifacts/implementation/iframe-resume-manual-20260917`. All 19 non-document source/binary/result files in the preceding [checkpoint manifest](C:/Users/user/animeapp/.artifacts/implementation/direct-stream-checkpoint.json) still match their hashes. No production code changed during this audit and no new build or full test run was performed. The last build remains **457 passed, 22 existing skips, zero failures**, with **23 Python cases passed**.

| Finding | Evidence | Conclusion and limit |
| --- | --- | --- |
| Native video becomes white after returning to its tab | At 02:12:09 local time, returned to the existing Mushoku player: white video, paused timeline about 11:27. Play at 02:12:34 fired LibVLC Playing; time advanced while the video remained white. Pause at 02:13:09 saved 721.9 seconds, about 12:01. | Confirmed rendering failure with a live media session. Missing source lookup is not the explanation for this occurrence. A standalone OS Alt-Tab cycle still needs separate testing. |
| Several player tabs already exist, but their view lifetime is wrong | The running app contains multiple native/browser playback tabs. `PlayMedia` and `PlayEmbed` create separate scoped player view models; the shell displays only `ActiveTab.ContentViewModel`. Selection does not pause the outgoing player. View unload detaches native video and disposes WebView. | Multiple view models are supported; retaining the right surface/document and pausing inactive playback are not established. View lifecycle is a confirmed defect boundary; the precise native HWND failure mechanism remains an implementation investigation. |
| Dub extraction returns a Japanese-labelled audio stream | Called production `do_extract` on the previously selected Miruro episode page with `audio=dub`, in an isolated browser profile. Result was native-ready. The captured PMT contains video PID `0x100` and one AAC audio PID `0x101`, ISO-639 language `jpn`. | The requested Dub label does not prove Dub. This route returned Japanese audio metadata and must not be accepted as verified English Dub. No independent listening check was performed. |
| Native Sub captions are not handed off | Earlier same-build native playback showed Japanese audio metadata with CC disabled; browser playback displayed English text. The current result contract has no external subtitle collection. This probe's playlist advertised no HLS subtitle group. | Confirmed caption-handoff gap on the demonstrated route. This does not establish that all providers lack subtitles. |
| Controls reserve layout space and duplicate site controls | `PlaybackRoot` contains a video row plus an Auto-height `ControlsLayer` row. Normal browser mode always reveals that panel; only browser PiP has a hiding special case. An existing Iron Man 2 page was visibly paused around 00:07 with duplicate app controls underneath. | Confirmed layout/product mismatch. That single movie page is not evidence of complete movie playback acceptance. |
| Source latency remains high | Previous native automatic anime lookup took about 43 seconds. Movie/TV UI still waits for the legacy all-results collector despite an existing progressive source API. | Stream MIME repair did not improve lookup speed. Use stage timing and existing incremental contracts before adding new systems. |

Frozen local evidence: [tab-switch events](C:/Users/user/animeapp/.artifacts/implementation/watchability-audit-20260928/tab-switch-events.log), [sanitized Dub extraction log](C:/Users/user/animeapp/.artifacts/implementation/watchability-audit-20260928/dub-extractor-sanitized.log), [Dub response summary](C:/Users/user/animeapp/.artifacts/implementation/watchability-audit-20260928/dub-summary.json), and [parsed audio-track evidence](C:/Users/user/animeapp/.artifacts/implementation/watchability-audit-20260928/dub-track-evidence.json). The PMT inspection runs offline on the bounded captured prefix. Raw captures contain signed URLs/request context, remain ignored local artifacts, and are not suitable for publication.

## A — Fix player tab lifetime and the white surface

Start here because subtitle, audio and fullscreen acceptance depend on a stable player. Existing [MainViewModel](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MainViewModel.cs:351) already creates separate players; keep that behavior. The [single active content host](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml:298), [DataContext rebinding](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs:666), and [unload cleanup](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs:406) must distinguish tab deactivation from final close.

1. Give each open playback tab its own retained view, native surface and browser document. Limit retention to actual open player tabs; do not retain every catalog/search view. Ensure WPF template reuse cannot attach one tab's native player or WebView document to another tab.
2. Pause the outgoing native player or selected browser video when changing tabs. Keep its media, tracks, position, request context and document available. Cancel an obsolete lookup only when its owning request is replaced/closed; another tab's operation must survive.
3. Keep hidden native overlay windows hidden with their tab. Keep browser video paused and preserve its selected iframe/document rather than disposing it during selection changes.
4. Reserve permanent frame unsubscription, WebView disposal, native handle release, proxy cancellation and scope disposal for actual tab close/app shutdown. Preserve earlier callback-after-close protections.
5. Reproduce OS Alt-Tab, app-tab changes, minimize/restore and fullscreen transitions separately. Determine whether preserving the surface fixes native repaint; add explicit activation/surface recovery only where still required, preserving the current position and pause state.

**Acceptance:** two different native episodes, two site players and a mixed native/site pair remain open. Repeated switching pauses the outgoing video, returns a visible frame at its retained position, and resumes without searching/reloading. Closing either active or inactive tabs leaves the others working. Test OS Alt-Tab and 20 repeated switch/close cycles; count native/WebView resources and verify memory settles after closure. A timeline advancing over white pixels fails.

## B — Make the player presentation consistent

The [current controls row](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml:151) has six subrows, including source/file inputs. [Auto-hide](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/PlaybackView.xaml.cs:531) collapses the row and therefore resizes video; entering fullscreen also needs to start the idle policy even if the video was already paused.

1. Put a compact native transport/seek/volume/audio/caption toolbar over the bottom of the video, with a readable gradient. Move URL/file/debug fields to the empty player or a menu. Keep windowed and fullscreen video bounds stable when controls fade.
2. Use the supported LibVLCSharp WPF overlay host, `VideoView.Content`. VideoLAN documents its native-window airspace constraint and its foreground overlay implementation; a sibling WPF grid alone is not a reliable overlay. [Official WPF guidance](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.WPF/README.md).
3. Reveal native controls on pointer movement and keyboard interaction; hide after the existing approximately three-second idle interval, including fullscreen. Keep controls visible while a menu, seek drag or keyboard focus needs them. Hide the cursor on idle over fullscreen video. Preserve accessible Play/Pause/seek/volume/captions/audio/fullscreen actions.
4. When `IsWebViewActive` is true, hide the whole app playback panel: no duplicate play/pause, timeline, volume, audio, captions, source/file row or status strip beneath the page. Remove the PiP-only exception and prevent mouse events/timers from revealing the panel. Maintain page input and existing telemetry/resume internally.
5. Make fullscreen fill the selected monitor and restore prior window bounds/chrome on exit; retain accessible Escape/Exit PiP and tab-menu recovery. Verify resize/DPI changes with the real native overlay rather than accepting XAML geometry alone.

**Acceptance:** native video does not resize when the toolbar hides; fullscreen has no reserved bottom row; movement reveals controls and idle hides them; menus remain usable. Site playback has no duplicate app panel in normal/fullscreen/PiP, while the site's controls and app exit/recovery actions still work. Test keyboard operation and dark/light themes.

## C — Carry real subtitles and verify actual Dub audio

The [scraper result](C:/Users/user/animeapp/UniversalMediaOS.Core/Services/ScraperEngine.cs:18) carries only media/request context. [CycleCaptions](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs:1213) can choose LibVLC subtitle tracks but cannot invent missing external captions.

1. Capture actual subtitle assets from the selected page/frame, media API or manifest: URL, label, language, format and required request context. Preserve embedded subtitle groups. Extend the existing Python result and C# handoff compatibly; old results without captions must remain readable.
2. Attach valid external captions to the native media as subtitle slaves/sidecars and expose real track choices/off state. Keep caption fetching scoped to that player's session; use the existing authenticated proxy where appropriate. Preserve selections through seek, retry, quality changes and episode navigation. Show an honest unavailable state when no usable captions exist.
3. Repair Dub selection before **every** stream-acceptance path. The initial-network guard already exists at [scraper.py](C:/Users/user/animeapp/UniversalMediaOS.Core/scraper.py:1752); this probe then accepted the default stream through Stage D at line 1766. Stage A/C/B and embedded-frame scans must not bypass the same audio selection/evidence requirement.
4. Return observed selected-audio evidence, separate from the requested mode. Reject a known Japanese-only result for English Dub. Resolve labelled Dub controls/server lists, wait for the resulting stream/document and choose a real English track when present. If Dub is unavailable, say so or retain a verified Dub browser fallback; do not silently present Sub as Dub.
5. Check the same work/season/episode in Sub and Dub. Compare selected page state, manifest/native track data and an actual audible scene; distinguish these evidence levels. Sub requires visible, timed English captions on Japanese playback. Dub requires audible English, not a Dub badge or a nonempty media URL.

**Acceptance:** deterministic fixtures cover captions with headers, no captions, multiple tracks, wrong-language autoplay in Stage D/iframe paths, cancellation and stale results. On the real app, timed Sub captions and audible Dub work through pause, seek, tab return and next episode. Caption/audio state must not leak across player tabs.

## D — Finish exact matching, improve lookup time and close the anime gate

Preserve automatic EverythingMoe/The Index discovery and the corrected TS payload validation. Reject wrong sequel/season/part/episode candidates before expensive browser extraction. Add independent candidate evidence; do not copy the requested title/audio/unit into a result and call it verified. Retain viable cached discovery/source context only for its actual work/unit/audio and handle expiring URLs.

Use recorded timings for index refresh, search, candidate scoring, browser navigation, audio selection and media validation. Remove repeated waits/duplicate navigation shown by those timings, prefer a ready verified result, and bound optional alternatives across open tabs. Do not obtain speed by merely tightening the timeout or increasing uncontrolled browser concurrency. Report first usable video time, not just first metadata time.

**Anime gate:** correct title/season/part and adjacent episodes; real Sub captions and audible Dub; native and genuine browser fallback; pause/seek/next/previous; retained multi-tab players; OS Alt-Tab; fullscreen/PiP; saved resume after close/restart/provider change; cancellation/failure/index outage; useful service health; bounded resource cleanup. Each row needs the exact executable/profile and observed result. Passing only the current Mushoku page does not establish all-provider availability or complete this gate.

## E — Prototype temporary episode downloads without replacing streaming by assumption

### What can be reused

[DualTrackerRssParser](C:/Users/user/animeapp/UniversalMediaOS.Core/Routing/DualTrackerRssParser.cs) already searches Nyaa RSS with AnimeTosho fallback. [SeasonDownloader](C:/Users/user/animeapp/UniversalMediaOS.Core/Archiving/SeasonDownloader.cs:639) already uses embedded **MonoTorrent 3.0.2**, with qBittorrent optional. No new mandatory key or external torrent app is needed for an embedded prototype.

The installed package's source commit is `e78faebd0aec117146cffccaaea987ab0629eec0`. Its [metadata-only API](https://github.com/alanmcgovern/monotorrent/blob/e78faebd0aec117146cffccaaea987ab0629eec0/src/MonoTorrent.Client/MonoTorrent.Client/ClientEngine.cs#L520) fetches torrent metadata separately; [file priority and selected-file progress](https://github.com/alanmcgovern/monotorrent/blob/e78faebd0aec117146cffccaaea987ab0629eec0/src/MonoTorrent.Client/MonoTorrent.Client/Managers/TorrentManager.cs#L77) support selecting files. In this version, `Progress`/`Complete` concern all files, so they cannot be the completion gate for a skipped-file batch. `PartialProgress` and selected-file hash/length validation are relevant.

### Why the existing season download is insufficient

- It chooses season batches and falls back to broader candidates; Dub filtering can retain generic releases. Part/season heuristics and first validated `LastCompletedVideoPath` cannot identify the requested episode reliably.
- qBittorrent/MonoTorrent start the torrent before selecting an episode and have no implemented skip-file policy. The MonoTorrent path waits for whole-torrent progress.
- The queue is persistent and serialized, retains partial files for restart, and is not owned by player tabs. Existing active-transfer fields represent a single current job.
- Metadata and stall limits exist, but a temporary job also needs a total deadline, disk budget and owned-path cleanup. A size-only fallback when ffprobe is absent is insufficient to establish video/audio/subtitle readiness.

### Small first prototype

1. Expose an explicit **Download this episode** / fallback action using the current frozen episode context. Search release metadata with exact identity/season/part/episode and requested audio; retrieve torrent metadata before fetching episode payload. If a batch has ambiguous filenames, require an explicit file choice instead of guessing.
2. Add the metadata-resolved torrent in a unique app-owned temporary directory while stopped. Mark all other files `DoNotDownload`, enable the requested episode and any explicitly selected sidecars, then start. Monitor selected-file completion and verified data, not whole-torrent percentage. Explain that shared BitTorrent pieces can include boundary bytes from adjacent files; do not promise zero unrelated bytes.
3. Reuse local LibVLC playback only after the complete selected file validates as video and its actual audio/captions satisfy the request. First prototype waits for completion; playback from incomplete torrent data is a separate, unselected expansion.
4. Give each temporary episode a player-tab lease. Pausing/switching tabs keeps the lease. Two tabs using one verified cached episode share it; close one and retain the file, close the last and stop/release player and torrent handles before deletion. Next-episode changes release the prior lease once no player needs it.
5. Bound metadata wait, no-progress wait, total transfer time, simultaneous jobs and disk space; expose cancel/progress/failure. Retry failed deletion and clean abandoned **owned** cache directories on startup. Check resolved paths remain inside that cache. Never delete a user's persistent Downloads, existing torrent jobs or arbitrary files.
6. Keep qBittorrent optional. If an adapter is useful later, identify the actual installed API version and add file-index/priority support; the current wrapper only lists names/sizes. The [official file-priority API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29#set-file-priority) supports that selection. Do not require qBittorrent credentials for normal use.

**Prototype gate:** a controlled seeded multi-file fixture downloads only the intended playable episode selection, exposes its real tracks, survives tab switches, and cleans up after last-tab closure/cancel/restart, without affecting permanent downloads. Record initial wait, transfer rate and disk usage. No torrent payload was downloaded during this audit; current Nyaa availability and release coverage are not established by repository support. Downloads can avoid expiring CDN URLs and preserve embedded MKV tracks, but seed availability, initial wait and matching still matter. Decide whether this becomes an optional backup or a broader replacement from those results.

## F — Movies, then Television: prove watchability

Keep Wikidata/TVmaze metadata and existing work/unit identity. Repair the actual source path: [FindSourcesAsync UI](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs:723) waits for the legacy collector, while [FindSourceUpdatesAsync](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualSourceResolver.cs:132) already exists. Connect it so a verified usable source appears before slow alternatives finish; preserve cancellation/selection-generation guards and useful partial results.

The [scraper provider](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ScraperAudiovisualSourceProvider.cs:104) forwards optional evidence but also populates identity/unit from the request and language from the requested/default language. The legacy UI calls results “exact.” Replace those claims with independent source evidence and observed audio/subtitle tracks. A configured source URL or metadata ID is not proof that a film or episode is watchable. Archive sources cover a limited set of declared public-domain/open-license items; provider source coverage remains a separate qualification task.

**Movie acceptance:** distinguish remakes, select the right feature/cut, play meaningful scenes, seek, verify actual audio/subtitles, pause/return/fullscreen, close/restart and resume under the stable feature key; test native and site-only routes and honest unavailable results. Retain the earlier Dune observations as limited evidence, not universal movie support. Fix poor relevance/latency/artwork from measured cases, including the missed `Your Name` result.

**TV acceptance:** select at least two seasons and adjacent episodes, reject wrong-season and unnumbered/special guesses, verify real audio/subtitles, next/previous, saved per-episode positions and provider changes. Repeat the shared player/tab checks. A TVmaze episode record is metadata, not a playable stream.

## G — Books with Anna's Archive, then Arabic cartoons

Anna's Archive is already integrated through [AnnasArchiveBookProvider](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Books/AnnasArchiveBookProvider.cs). The missing proof is exact edition/file resolution and reading, not another catalog abstraction. Cross-provider lookup currently takes the first ISBN/title/author result, risking an edition mismatch. All returned Anna assets are marked `PublicDomain` and downloadable at [line 164](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Books/AnnasArchiveBookProvider.cs:164), while [BookReaderService](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Books/BookReaderService.cs:275) treats that access flag as meaningful. Indexing a file does not establish its rights; carry actual access/availability evidence rather than manufacturing that label.

Prioritize Anna discovery, exact hash/ISBN/edition/language/format matching and actual EPUB/PDF resolution. Publish fast results without waiting for every catalog, distinguish available file/external page/preview/unavailable, verify format bytes and file completeness, then test reader rendering, navigation and restart progress. Preserve local imports and existing reading data. Web research could not confirm a currently reachable canonical Anna mirror/FAQ; do not interpret that tool failure as a general outage or introduce a mandatory key. Test the configured free route directly during this batch.

Keep Cartoons deferred. Qualify a small target sample including Flona and Heidi using Arabic and alternative titles, actual Arabic audio evidence, the correct dubbed version and episode order. Prefer reusing verified anime/TV playback and identity where possible. General animation metadata or Japanese originals do not satisfy this request. Preserve the category and saved data until the user chooses retention/removal; no standalone new cartoon scraping framework is required by this audit.

## Remaining checklist work reconciled

This maps the existing open packages to concrete remaining outcomes. Specifications already written in the [implementation plan](C:/Users/user/animeapp/docs/IMPLEMENTATION_PLAN.md) remain useful, but implement additional machinery only where an observed failure or a required acceptance check justifies it.

| Existing package / requirement | What still needs doing | When |
| --- | --- | --- |
| IP01 / R01 | Visible build identity/executable; structured stage timing; controlled stalled-process/service fixtures; isolation coverage outside current tested paths. | Alongside each affected repair and before release. |
| IP02 / R03 | Real keyboard, popup, focus/hover/disabled and density/accessibility checks, including the new native overlay. | B, then final UI pass. |
| IP03 / R02 | Player view/tab/window lifetime, OS focus/resize, monitor/DPI, RTL/localization and window dragging. | A/B first; remaining locale/DPI before release. |
| IP04/IP07/IP08 / R05–R06 | Exact specials/provider episode mapping, independent Python/manifest/Archive source evidence, matching and persisted canonical identities. | C/D and F. |
| IP05/IP06 / R04–R05 | Film/animated-film relevance and availability, artwork, actual paging/accessibility, legacy metadata-setting/provider-ID reconciliation; Cartoon routing. | F; Arabic Cartoon decision last. |
| IP07/IP10 / R06–R07 | Actual UI consumption of progressive verified sources, bound provider work across tabs, stage timing and useful cancellation/partial results. | D/F. |
| IP08 / R05/R13 | Already-canonical duplicate reconciliation, verified legacy SQLite aliases, last-unit summaries, real migration and rollback. Preserve ambiguous rows without guessed merges. | Alongside F and before release. |
| IP09 / R09 | Runtime/script/dependency revision invalidation, CLI protocol failures, awaited shutdown/owned-process cleanup and packaged Repair services checks. Revisit the historical staging denial only if it recurs. | Needed playback/service failures first; final package pass. |
| IP11 / R08/R15 | Native subtitles, actual Dub, exact units, index outage/rotation, measured first video, retained tabs and close/reopen/provider-change resume. | A–D, anime gate. |
| IP12 / R10 | HLS session cancellation, bounded request ownership, body-inclusive manifest/key deadlines and media inactivity limits. Current `CopyToAsync` has no session token; a removed session does not cancel ongoing bodies. Do not cap healthy long video with a short total timeout. | Shared playback repair before gate/release; two-player tests. |
| IP13 / R11 | Progressive book pages and Dub checks with truthful unknown states. Both `BookCatalogService.SearchAsync` and Dub filtering still await all tasks; default/Any browsing must stay responsive. | Dub in D; books in G. |
| IP14 / R12 | Image unload/reload ownership, shared in-flight/concurrency limits and body deadlines; virtualized growing audiovisual grid. Decode-width cache identity and background decoding already exist: retain them. | Measured catalog stalls/large-list checks in F/G. |
| IP15 / R13 | Measure persistence contention; synchronous resume load/final writes and favorite JSON remain. Keep immutable per-tab keys, ordered final writes and bounded flush off the UI dispatcher; preserve current ordering guards. | Multi-player/resume fixes and final contention check. |
| IP16 / R14 | Settings saved/reloaded/migrated accurately, health/Repair truthful, native/site actions meaningful, and complete visible button results. | A–G as changed; remaining shell pass. |
| IP17 / R15–R16 | Full native/browser/anime/movie/TV/reading journeys; download restart/pause/resume/cancel; My List; manga; optional MAL/voice/notification paths; two-client Watch Together reconnect; repeated resource cycles. Preservation checks do not imply every feature is defective. | Each feature gate, then surrounding-feature pass. |
| IP18 / R17 | Inventory 22 skipped tests, cover release-relevant gaps or state exclusions; exact source manifest; real publish/package; fresh and copied legacy profiles; dependency/scripts/assets inclusion; backup/rollback notes. | Final release gate. |

## Next implementation batch

Implement A as a small player-host/lifecycle change, with targeted native/browser multi-tab tests and the live white-screen reproduction. Then B removes the duplicate site panel and introduces the real native overlay. Do not mark the anime gate complete until C/D also pass. Investigate remaining shared HLS/persistence failures as they affect these batches; defer unrelated architecture expansion. After every batch, record the exact build, actual observations and remaining failures in the single active checklist.
