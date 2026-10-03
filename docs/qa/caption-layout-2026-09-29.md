# Native caption placement and fullscreen checkpoint

**September 29, 2026. Scope:** B/C / IP02/IP03/IP11 caption overlap and native presentation checks. Recovery, anime and release acceptance remain incomplete.

## Changes

Move the title and Playback options to the top of the native overlay. Options have a bounded scroll container above the transport, so expanding them cannot cover the default bottom caption area. Keep a compact transport overlay, raised above that area when a caption is selected. Account for the fitted picture and letterboxing using native video dimensions; calculate clearance in WPF units when the viewport or selected caption changes. Caption Off returns transport to the bottom.

Keep the native renderer, selected caption files and existing reload behavior. Showing, hiding or expanding controls changes overlay layout without changing the video bounds or replacing media. Treat interaction with the three actual panels as control interaction; the full-size transparent overlay must not prevent idle hiding or swallow video double-clicks. Site mode still hides the entire app panel.

[VLC 3's public video API](https://videolan.videolan.me/vlc-3.0/group__libvlc__video.html) has caption selection/delay but no live position setter. Its [subtitle margin implementation](https://raw.githubusercontent.com/videolan/vlc-3.0/master/src/video_output/vout_subpictures.c) uses a native output margin. This change avoids private VLC APIs, forced stream reloads on pointer movement and a new subtitle renderer.

## Verification

Production Debug build: **zero warnings/errors**. **483 selected background C# cases passed, zero failures/skips**, including four added layout cases. Those cases retain the production panel/grid/expander/scroll constraints with sized substitute contents and check small, normal, fullscreen and letterboxed viewports: options stay above transport, transport clears the bottom caption band and expansion/Off do not change video size. Existing native/site visibility and input regressions pass.

The selected suite excludes PlaybackPresentationTests, RetainedPlayerTabsTests, BootWindowTests, DownloadTests, MangaTests, NavigationTests, SearchTests and SettingsTests while the user works on the primary monitor. Python, routing, scraping and persistence were unchanged; the preceding 48 Python passes are historical evidence, not a rerun.

Final production assemblies were copied into private, muted diagnostics with isolated profiles, secondary-monitor startup, ShowActivated=false and automatic shutdown. Timers invoked production commands and view methods; read-only Computer Use inspected their actual rendered windows. No agent OS keyboard, mouse or activation input was used. Diagnostics ran sequentially, avoiding overlapping native video windows. Normal placement was 1280×800 at (2880,478); fullscreen filled the actual secondary monitor, **1920×1080 at (2560,362)**.

- **Controlled native fixture:** 255 samples, no playback/action errors. Arabic cues rendered beneath visible transport with expanded options in normal and paused fullscreen. Fullscreen playing idle/reveal kept 1920×1080 video bounds and the same media object; idle collapsed controls, disabled their hit testing and hid the cursor. Pointer-handler reveal and pause restored controls. Paused native PiP rendered video at 560×390, and exit restored the app. Caption Off returned clearance to zero.
- **Real anime journey:** 279 samples, no playback/action errors. Actual Arabic dialogue rendered clearly with expanded controls in paused fullscreen at 214.022 seconds. Playing fullscreen advanced, hid/revealed controls, paused and returned to the same window/media; native PiP/exit and Off/English selection completed. Off had zero transport clearance; caption/Off selection still uses the preceding checkpoint's native input reload and small decoder advance.
- **Focused real frame check:** 202 samples, no playback/action errors. The English hold visibly rendered dialogue at 214.711 seconds with English selected, paused video and expanded production options. An actual fullscreen playing frame showed video without app chrome or controls after idle. Native clock, geometry, visibility and media identity are recorded independently in the trace. The pointer reveal calls the production mouse handler; this does not constitute injected mouse or keyboard/focus coverage.

The actual-source diagnostics supply the previously captured Frieren Sub result to the normal router/proxy/player. They reuse the actual stream and its provider caption files; fresh catalog/search/extraction and independent exact-unit verification are bypassed. The local fixture bypasses the public-network proxy restrictions and uses existing HLS/tone/VTT test data. No new mandatory keys or provider selection system was added. Diagnostic service health stays pending because AutoManageServices=false; it is not service acceptance.

## Limits and next checks

This closes the demonstrated overlap for default bottom captions on the tested routes. Author-positioned/styled captions, unusual multiline cues and mixed embedded/provider lists still need qualification. The first fixture's initial English seek hold did not render a cue; its Arabic hold and the focused real English hold establish the scoped frame results. Continuous fixture cue behavior is not certified. A frame request after an earlier diagnostic's timed shutdown returned no foreground process; the later focused hold provided the missing English and idle-frame evidence.

Real playing Alt-Tab/minimize/restore, physical keyboard/selector/source-edit interaction, browser PiP/recovery and monitor/DPI transitions remain open. Preserve the broader A/B/C gates. Continue D's exact season/part/episode matching, automatic lookup latency, resume and cleanup; then the agreed temporary downloads, native Movie/TV, Anna's Archive and Arabic-dubbed anime work.

## Artifacts

[Executable](C:/Users/user/animeapp/.artifacts/implementation/caption-layout-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [selected test results](C:/Users/user/animeapp/.artifacts/implementation/caption-layout-20260929/results/caption-layout-verified.trx), [build log](C:/Users/user/animeapp/.artifacts/implementation/caption-layout-20260929/build.log), [manifest](C:/Users/user/animeapp/.artifacts/implementation/caption-layout-checkpoint.json), [fixture summary](C:/Users/user/animeapp/.artifacts/implementation/caption-layout-20260929/native-fixture-summary.json), [real journey summary](C:/Users/user/animeapp/.artifacts/implementation/caption-layout-20260929/native-real-profile-summary.json), [frame summary](C:/Users/user/animeapp/.artifacts/implementation/caption-layout-20260929/native-frame-profile-summary.json). Traces, isolated profiles, launch records, private driver/runtime copies and the before snapshot are retained beside those summaries. Computer Use frames are displayed in the conversation, not exported screenshot files.
