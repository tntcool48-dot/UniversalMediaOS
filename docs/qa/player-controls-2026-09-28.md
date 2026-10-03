# Native player controls checkpoint — September 28, 2026

Batch B's native overlay and keyboard implementation is complete. Its broader live presentation acceptance remains open. No caption, Dub extraction or provider-speed repair is included here.

## Change and automated evidence

The native transport panel now belongs to `VideoView`'s content overlay. The playback root has no separate controls row: video fills the available area whether controls are visible or hidden. The compact panel keeps transport on the left and volume/presentation on the right; speed, quality, audio, captions, episode actions and source entry remain under Playback options. Hover text and keyboard focus are readable.

While native video is playing, the panel fades after three seconds without interaction and its cursor hides. Pausing, buffering, errors, tab return and interaction reveal it. Seeking, an expanded options section or keyboard interaction with controls prevent hiding. A reveal generation prevents an old fade completion from collapsing newly revealed controls. Native overlays independently follow their tab and native/site mode.

VLC's WPF overlay uses its own foreground window. Input therefore needs both overlay handlers and an active-tab owner-window shortcut handler; the latter also handles owner focus restored outside the video. Browser page input is excluded. F and Escape were tested after owner-tab focus and while editing source text. Browser PiP can be requested by focusing its selected tab and pressing P; its actual final-build desktop acceptance is still pending. The tab tooltip explains this action. Site playback continues to hide the app transport/source panel.

The separate-window behavior was checked against the [upstream ForegroundWindow source](https://raw.githubusercontent.com/videolan/libvlcsharp/3.x/src/LibVLCSharp.WPF/ForegroundWindow.cs). The implementation and results below concern the repository's installed LibVLCSharp build.

The final solution build has **zero warnings and errors**. The final suite has **472 passed, 22 existing skips, zero failures; 494 total**. Compared with the preceding 470-passed checkpoint, there is one additional small-viewport geometry case and one owner-focus/source-editing shortcut regression. Existing native handle retention, disposal, browser input, presentation and surrounding-feature tests also passed. Python was unchanged and was not rerun; its previous 23-case result remains separate.

Artifacts: [executable](C:/Users/user/animeapp/.artifacts/implementation/player-controls-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [final TRX](C:/Users/user/animeapp/.artifacts/implementation/player-controls-build/results/player-controls-final.trx), [final test log](C:/Users/user/animeapp/.artifacts/implementation/player-controls-build/results/final-suite.log), [build log](C:/Users/user/animeapp/.artifacts/implementation/player-controls-build/results/build.log), and [source/build/evidence manifest](C:/Users/user/animeapp/.artifacts/implementation/player-controls-checkpoint.json).

## Controlled desktop evidence and limits

The isolated fixture is `.artifacts/implementation/player-controls-live-20260928`. It references the exact production assemblies and opens two generated local H.264 MP4 sources plus two same-origin iframe/WebM pages. A read-only timer samples production video bounds, overlay/control visibility, cursor, player time and window geometry. All input was separate Computer Use interaction. The fixture does not call provider discovery; its silent media cannot establish anime identity, captions or English Dub. Automatic service preparation was disabled for this presentation fixture; the health bar is not service-availability evidence.

| Check | Observed result |
| --- | --- |
| Native overlay and idle | Native pattern rendered with controls above it. Candidate and final traces preserve 1280×653 video/overlay bounds during idle hiding; controls have opacity zero, hit testing disabled and cursor None. Four final samples at 13:30:17–20 UTC show native time advancing from 3.152 to 5.901 seconds with this hidden state; rendered frames were observed before and after it. Geometry tests also cover 900×500 and 360×240 layout. |
| Native keyboard fix | Candidate K did not pause after owner activation. The final build started native playback with K from tab focus, then another K paused a visible frame at 00:07.250/frame 174. The frame stayed visible through subsequent window/presentation changes. |
| Native fullscreen | On the final build, the app fullscreen button filled the 2560×1440 monitor with the paused native frame; app title/navigation/tab/status rows were absent and controls overlaid the bottom. The read-only trace confirms matching 2560×1440 video/overlay bounds. This was a paused-video check, not fullscreen idle playback. |
| Site panel | Both final-build iframe pages loaded; Browser 2 rendered and advanced using page controls. No app transport/source panel appeared. Final native/site PiP and site recovery were not reached. |
| Resize/return | Live input changed the normal window to 900×560. The paused native frame remained visible, with video/overlay bounds 900×373. This is an observed resize, not a controlled DPI or monitor transition. |
| Escape/interruption | The user's physical Escape stopped Computer Use. The production log records fullscreen restoration with Escape, and subsequent read-only samples show a normal 900×560 window with the paused frame. No further desktop input was issued. This does not complete the remaining scripted matrix. |

Some desktop input overlapped the checks; observations were refreshed before retrying. A few occluded transparent-window captures showed underlying desktop content; only the re-observed visible app frames support rendering claims. Unattributed user fullscreen/resize actions are not counted as automation successes. The final attempted fullscreen Play action was stopped before it ran.

The [frozen final trace](C:/Users/user/animeapp/.artifacts/implementation/player-controls-live-20260928/presentation-final-snapshot.jsonl), [frozen app log](C:/Users/user/animeapp/.artifacts/implementation/player-controls-live-20260928/live-log-final-snapshot.txt), [launch record](C:/Users/user/animeapp/.artifacts/implementation/player-controls-live-20260928/launch.json) and copied fixture assembly are hashed in the manifest. The launch record's production assembly SHA matches the final build. The final diagnostic app was left open after the user stopped input; the temporary HTTP server was stopped. Browser fixture pages therefore should not be reloaded until that local server is restarted. The executable linked above is the normal test app; the open diagnostic app bypasses discovery.

## Remaining acceptance

Finish playing fullscreen idle/reveal, seek/selector/editing interaction, native and site PiP/exit/restore and visible-frame checks on the final build. Verify real anime playing focus/minimize/restore, monitor/DPI changes and resource settling separately. Automated empty-player presentation transitions do not establish these rendered-video journeys.

The next implementation work is batch C: carry actual external subtitles into native playback and reject/repair the reproduced Japanese-only stream accepted for Dub. Anime's exact-unit, performance and full watchability gate remains open, followed by Movies/TV and the other checklist items.
