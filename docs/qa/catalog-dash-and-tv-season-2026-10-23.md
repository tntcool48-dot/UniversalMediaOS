# Catalog DASH availability and a second complete TV season — October 23, 2026

Source **e744dcd** (equal to `origin/main`), Debug build in the worktree's own `.artifacts/implementation/build`. Small evidence is in [catalog-dash-tv-20261023](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023).

## Catalog DASH: no provider the app uses serves it

### Which providers can reach a catalog item

The resolver (`AudiovisualSourceResolver`) combines three provider families for Movies, TV and Cartoons:

- **Internet Archive** selects only progressive files: `.mp4`, `.m4v`, `.webm`, `.ogv`, `.mkv` or an MPEG-4/H.264/Matroska/WebM/Ogg format label (`InternetArchiveSourceProvider.IsSupportedVideoFile`). It never selects a manifest, by design.
- **Configured JSON-index providers** are empty by default (`ProviderIndexLocations`), because the user's rule keeps keyless Wikidata/TVmaze catalogs without a required provider. The October 9 adapter repair shows an MPD handed over with its content type through stub responses, but no real index is configured.
- **The built-in scraper** builds four embed routes from the catalog's IMDb ID (`build_embed_results`: vidsrc, 2embed.cc, multiembed.mov, autoembed.co). For vidsrc it also tries the vidsrc mirrors.

So only the scraper's embed players could deliver DASH for a normal catalog item.

### Survey of the scraper's real players

`dash_survey.py` (kept beside the evidence) loads each route in the scraper's own off-screen Chromium configuration and starts the player with the scraper's own control clicks. It then records, for 22 seconds, every response in every page and iframe target through the DevTools network domain. For each response it records the MIME type, URL suffix and, for small text-like XHR/fetch bodies, whether the body starts as an HLS playlist (`#EXTM3U`) or an MPD (`<MPD` / `urn:mpeg:dash`). It also lists the player libraries in each frame (hls.js, dash.js, Shaka, JW Player, video.js, Plyr, Artplayer, Clappr) and any loaded `<video>`. Only hosts and classifications are kept; signed paths are not.

Items: Big Buck Bunny (2008), Sintel (2010), Night of the Living Dead (1968), Nosferatu (1922), Dune (2021), Breaking Bad S1E1 and The Office (US) S1E1. Routes: vidsrc.to, 2embed.cc, multiembed.mov, autoembed.co, vidsrc.me and vidsrc.io. vidsrc.cc (timeout), vidsrc.net (certificate failure) and vidsrc.xyz (no DNS) did not answer a plain request and were not loaded.

| Result | Page loads |
|---|---|
| Total | **42** (7 items × 6 routes) |
| Player loaded a video (MSE `blob:` source with the item's duration) | 27 |
| HLS manifest observed (URL, MIME or `#EXTM3U` body) | 24 |
| **DASH observed** (`.mpd`, `application/dash+xml`, MPD body, dash.js or Shaka) | **0** |
| Player libraries seen anywhere | hls.js only |

Three loads played video under hls.js without a recorded manifest response (vidsrc.me Sintel, multiembed Night of the Living Dead and The Office). Their manifest type is unknown rather than DASH, since hls.js plays only HLS. 2embed.cc never started a player (0 / 7). [Sanitized survey](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/provider-format-survey.json).

Earlier probes agree: three Big Buck Bunny Archive items listed no `.mpd` (October 9), and Commons' Big Buck Bunny advertised only WebM/QuickTime renditions (October 16). The official Shaka Sintel MPD and BBC R&D streams are engineering samples without a catalog binding. The app also refuses Shaka's indexed single-file (`SegmentBase`) form.

### Accepted exclusion (approved by the user, 2026-10-23)

The user approved this exclusion on October 23, 2026: "Accept catalog DASH as a documented exclusion".

> Independently matched **catalog** DASH Watch via download/native playback is excluded from this checklist item. None of the app's real providers serves DASH for a catalog item: Internet Archive is progressive-only by design, no JSON-index provider is configured by default, and the four embed routes and vidsrc mirrors played HLS in every load that reached media. DASH transport itself stays qualified by the public finite sample with native frames and the controlled finite-DASH remux/decode/ownership checks ([DASH native QA](dash-native-validation-2026-10-03.md)). Reopen if a provider, such as a user-configured index, offers an MPD for a matched catalog item.

Catalog DASH is therefore closed for this item as an accepted exclusion, not as a passed journey. No code was changed for it.

## Second complete TV season

### Choosing the season

A freely licensed series came first. **Pioneer One** (TVmaze 4908, IMDb tt1748166, CC BY-NC-SA, Season 1 = 6 numbered episodes) cannot be completed through the app today:

- The scraper's real resolver (`audiovisual_scraper.py resolve-verified`, the same mode the season download uses) returned only a website fallback for vidsrc S1E1 and S1E2, autoembed S1E1 and multiembed S1E1. No native media and no item evidence.
- Internet Archive holds the episodes ("Pioneer One S01E01", "PIONEER ONE Ep. 1 Earthfall Pilot", a "Pioneer One" collection item), but none carries structured season/episode fields. The Archive provider correctly refuses to read episode numbers out of free-text titles. Under the rules this stays unavailable; no matching was relaxed.

Public-domain series with complete seasons on TVmaze have 22–39 episodes per season (*Sherlock Holmes* 1954, *Rocky Jones*, *One Step Beyond*), which is not a bounded sample. The sample is therefore **The Office (US) Season 1**: TVmaze show 526, IMDb tt0386676, six numbered episodes of about 22 minutes. A resolver pre-check of S1E1 returned validated HLS with independent provider item evidence: title "The Office (US)", 2005, tt0386676, S1E1, English subtitles.

### Real-app journey

Debug build of e744dcd plus the repair below (WPF executable SHA-256 `D5521D57…1A0D58` before the repair). Isolated data root under the session scratchpad; `AutoManageServices=false`. Window on the second monitor at (2552,354), 1936×1048. The journey was driven through UI Automation with the normal controls: TV Shows → search "The Office" → the 2005 card → Season 1 → **Download season**. [Observations](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/office-s1/observations.jsonl), [drivers](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/drivers).

Download season ran from 02:36:48 to 02:42:11 UTC (about 5½ minutes) and ended **"Saved 6 episodes from Season 1, including available captions"**. Each episode took one verified source on its first attempt (lookup about 25 s, transfer about 25 s); no failover was needed.

| Unit | TVmaze title | Media bytes | Duration (s) | English VTT cues | Last cue starts |
|---|---|---|---|---|---|
| S1E1 | Pilot | 93,095,217 | 1382.609 | 551 | 00:22:05.305 |
| S1E2 | Diversity Day | 81,480,576 | 1307.562 | 529 | 00:21:13.305 |
| S1E3 | Health Care | 87,421,004 | 1323.282 | 532 | 00:21:32.305 |
| S1E4 | The Alliance | 89,041,491 | 1320.449 | 582 | 00:21:28.305 |
| S1E5 | Basketball | 82,884,963 | 1324.094 | 453 | 00:21:31.305 |
| S1E6 | Hot Girl | 82,897,823 | 1325.999 | 569 | 00:21:32.305 |

- **Bounded transfer:** 516,821,074 bytes in total, all H.264 + AAC in Matroska.
- **Exact item/unit:** every `library-item.json` records `av:tvmaze:show:526` and `season:1:episode:N`, with the catalog identity (The Office, 2005, tt0386676), the TVmaze episode title and `SourceProvider` `scraper-vidsrc`. [Metadata copies](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/office-s1/metadata), [probe results](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/office-s1/library-results.json).
- **Audio stays honestly unknown:** the AAC streams carry no language tag, `AudioEvidence` is null, and each file keeps the notice "Audio language unverified; check Audio in the player." Spoken language was not listened to.
- **Captions:** one English VTT per episode (28.9–36.6 KB). S1E1's first cue, at 00:00:06, is a subtitle site's advertising line, not dialogue. All six files start their first cue at that same time, which suggests the same insertion, but the others were not inspected before the media was deleted.

### Fault reproduced: a saved episode in a deep folder would not play

Downloads → S01E03 → Play showed **"Playback problem — The selected stream could not be played"** with 00:00 / 00:00. The app log shows the local file handed to LibVLC as a `file:///` URI, then an immediate playback error. The saved media path was **276 characters**. .NET wrote it and ffprobe validated it, but LibVLC 3.0.23 opens local files through Win32 calls limited to MAX_PATH (260). The app manifest does not declare `longPathAware`, and that would not help on default Windows installs anyway. Any user whose download folder is about 150 characters deep (for example a nested OneDrive or project folder) gets a season the app reports as saved but cannot play. The same applies to its sidecar captions.

- **Regression before the repair:** the new `LongLocalMediaPathTests` decode a generated H.264/AAC file and a VTT through the app's own engine from a path of at least 280 characters. **1 failed, 1 passed.** The engine log reads "cannot open file … Your input can't be opened."
- **Repair:** `NativeLocalPath` gives LibVLC the volume's 8.3 short form when a local media or caption path reaches 260 characters; shorter paths keep their unchanged `file:///` URI. If a volume has no short names, or the short form is still too long, the player now says "This file's folder path is too long for the native player. Move it, or choose a shorter download folder in Settings." It no longer reports a generic stream failure. Captions without a usable path are left off rather than guessed. `PlaybackViewModel` uses the helper for local media, downloaded sidecar captions and prepared embedded-caption files.
- **After the repair:** **4 / 4** focused checks pass: long-path decode with caption, unchanged ordinary paths, and two refusal cases. The related cohort passes **132 / 132** in 1 min 7 s, zero failures or skips: `SubtitleHandoffTests`, `NativeShortMediaCompletionTests`, `SeasonLibraryDownloadTests`, `DashNativeStreamingTests` and `TemporaryEpisodeWatchTests`. The build had zero warnings and zero errors. [After TRX](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/long-path-after.trx), [related TRX](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/local-playback-related.trx). The launcher's fixed results file name overwrote the before-repair TRX; its counts and engine message are recorded here.

### Native playback, captions and restart after the repair

The same isolated profile was used, with the same 276-character paths:

- **First open:** the rebuilt app (PID 10032) opened Downloads → S01E03, decoded video and showed an English caption while playing. Space paused at **185.795 s** of 1,323.282 s, matching ffprobe.
- **Paused seeks:** to **660.000 s** (11:00) and **1,250.000 s** (20:50). Each showed a new decoded frame with an English cue, still Paused, titled "The Office · S01E03 · Health Care".
- **Restart:** after a normal close, new process 5928 reopened S01E03 from Downloads. The log reads "Loaded saved position 1250.0s for media 'av:tvmaze:show:526', episode 'season:1:episode:3'", and the short-path URI was handed to LibVLC. There was no provider lookup or transfer.
- **Separate unit:** S01E01 opened in its own tab at its own position (paused at 8.953 s of 1,382.609 s) without disturbing S01E03's tab.
- **Final resume rows:** S01E03 = **1,266.792 s** and S01E01 = **8.953 s**, both under the canonical work key. [Rows](C:/Users/user/animeapp/.artifacts/implementation/catalog-dash-tv-20261023/office-s1/resume-rows.json). Every app process closed normally.

The 517 MB of media, the profile and the browser profiles were deleted after this evidence was recorded. Screenshots, metadata, probes and observations remain. No real profile, permanent download or protected QA row was used.

## Limits and follow-ups

- **What remains open in this item:**
  - Spoken audio stays unknown for both qualified seasons (Breaking Bad S1, The Office S1), and so does specified-audio season acceptance.
  - Freely licensed and public-domain seasons are unavailable through the current providers.
  - These cue checks do not certify synchronization throughout each episode.
- **Seen outside this item, not fixed here:**
  - The TV Season and Episode list items expose the record `ToString()` (for example `AudiovisualSeasonChoice { Number = 1, DisplayName = Season 1 }`) as their accessible name.
  - The app log writes five of six provider manifest URLs with their signed path segments unredacted.
  - While native video plays, the overlay controls are absent from the accessibility tree, and synthetic pointer movement did not reveal them; Space did.
- **Accessibility tooling note:** the managed UI Automation client sees the overlay controls only while paused, so the drivers used FlaUI's UIA3 client.
