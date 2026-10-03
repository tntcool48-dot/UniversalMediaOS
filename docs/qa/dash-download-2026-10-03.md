# Bounded DASH temporary download — October 3, 2026

## Problem and fix

Watch via download rejected every MPD. The temporary download service now accepts completed, unprotected MP4-based DASH with one finite period, one video group and bounded audio groups. It resolves inherited BaseURLs, SegmentTemplate Number/Time/RepresentationID/Bandwidth references and SegmentList byte ranges, then downloads initialization/media resources under the existing public-network, redirect/header, size, disk, inactivity and overall deadlines. It rejects protected/live/indexed/external XML, ambiguous groups, unsupported embedded tracks, gaps/overlap, unsupported offsets/tokens and excessive schedules.

Only owned local asset names enter a newly built local MPD. FFmpeg assembles the selected video and retained audio tracks into the short local MKV; the existing duration/media probe, caption handoff, cache and final-owner deletion apply. Provider XML is not passed to FFmpeg. MP4 signatures are an initial guard; actual remux/probe is required before handoff.

## Verification

Build: zero warnings/errors. Focused download/workflow checks: **53 passed**. Full C# run: **647 passed, zero failures, 22 existing skips, 669 total**, 2 minutes 11 seconds. [Results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/dash-download-full-2026-10-03.trx). Python remains unchanged from its earlier 156-pass baseline.

New cases cover formatted Number and Time templates, inherited descriptors, highest video choice, two audio languages, finite negative repeats, shared-file byte ranges, cancellation during assembly, and ten unsafe/incomplete inputs. A real FFmpeg-generated four-second fixture passed the production downloader/remux/probe, then decoded with one video and two audio tracks tagged ENG/JPN. Its two owners reused the same completed file without another request, retained it after the first close and removed it after the final close. These are generated sine tracks; this does not establish spoken language. The first fixture run failed because FFmpeg's Windows DASH muxer wrote segment paths relative to the current directory when given a backslash output path. The fixture now uses forward slashes, keeping generated resources in its isolated directory.

## Limits and next step

This batch established controlled byte/remux/decode and ownership evidence. A later October 3 batch added actual MP4 initialization/media sampling in Python and qualified a public finite DASH native stream on the second monitor, including a LibVLC initialization-header repair. See the [current DASH validation QA](C:/Users/user/animeapp/docs/qa/dash-native-validation-2026-10-03.md). Independently matched catalog DASH download/native playback, provider cuts/audio/full captions and unsupported formats remain open. Actual second-monitor HLS film/TV evidence remains in the [playlist QA](C:/Users/user/animeapp/docs/qa/movie-tv-playlist-download-2026-10-02.md).

TV season/library saves were subsequently implemented and physically checked; see [season/library QA](C:/Users/user/animeapp/docs/qa/season-library-download-2026-10-03.md). That run retained six completed episodes when episode 7 failed, so full-season availability remains open. Use the implementation checklist for the current next action. No grouped checklist gate closes from this scoped DASH batch.
