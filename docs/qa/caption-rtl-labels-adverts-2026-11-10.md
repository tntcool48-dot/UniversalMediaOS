# Right-to-left captions on every path, same-language labels and subtitle-site adverts — November 10, 2026

Second pass on the "Watch via download" caption item, on branch **claude/download-captions-7abd2a** after it was fast-forwarded to the merged **401155b** (`codex/app-recovery`, all ten sessions' work). First pass: [caption languages QA](download-caption-languages-2026-10-23.md).

## Merged code

The merged source built with no warnings or errors. My seventeen first-pass caption regressions (language tags, saved-caption names and labels, menu order, saved over embedded, right-to-left saver) passed **17 / 17** on it (`merged-401155b-caption-regressions-20261110.trx`). Nothing needed repair after the merge.

## Fault 1: right-to-left punctuation on streamed, local and embedded captions

LibVLC lays every caption line out left to right, so an Arabic line's final period, question mark or ellipsis shows at the right end, beside its first word. On October 24 only captions the app saves from a provider got right-to-left marks. Three paths still showed it:

- **Streamed provider captions**, served to LibVLC through the app's loopback proxy.
- **Local caption files the app did not write**, such as a torrent's `episode-0001.ar.srt` beside the video.
- **Embedded right-to-left tracks** in a local MKV, shown from the extracted copy that keeps cues correct after a paused seek.

**Repair (55ee652).** One helper, `CaptionLanguage.MarkRightToLeftCaption`, adds a right-to-left mark at both ends of each cue text line of a SubRip or WebVTT file. It leaves ASS alone (libass lays it out itself) and returns nothing for bytes that are not UTF-8.

- The proxy marks a caption it serves when the track's declared language or label is right-to-left.
- A local right-to-left caption file is shown from an owned marked copy under `NativeCaptions`; the copy goes with its input, and the original file is never changed.
- The extracted copy of an embedded right-to-left track is marked. A remembered right-to-left track now prepares that copy on the next input without another choice; before, a remembered choice showed the original track unmarked.
- The saver now uses the same helper. It used to decode leniently, which would have corrupted an Arabic file in Windows-1256 (a common encoding for Arabic SubRip); such a file is now kept byte for byte, unmarked.

**Checks.** Five new cases, each failing before the repair (`rtl-all-paths-before-repair-20261110.trx`, `-2-`):

- `SubtitleHandoffTests.StreamedRightToLeftCaptionsKeepTheirPunctuationOnTheirSide`: the proxy serves the Arabic VTT marked and English unchanged.
- `SubtitleHandoffTests.LocalRightToLeftCaptionFilesShowFromAMarkedCopyAndKeepTheOriginal`: the `.ar.srt` sidecar is shown from a marked copy, the original keeps its bytes, the copy is removed when the input changes, and a Windows-1256 `.ara.srt` reaches LibVLC unchanged.
- `NativeShortMediaCompletionTests.EmbeddedRightToLeftCaptionsShowFromAMarkedCopyAlsoWhenRemembered`: a real `ara` track in an MKV, chosen and then remembered on the next input.
- `PlaylistMediaDownloadTests.SavedRightToLeftCaptionInALegacyCodePageIsKeptByteForByte`.

One embedded assertion passed before the repair at first. xUnit's `Assert.Contains(string, string)` compares culturally, and culture-sensitive comparison ignores invisible marks such as U+200F, so it could not fail. It now compares ordinally and failed as expected. My earlier mark checks used `Assert.Equal`, which compares ordinally.

After the repair the five pass **3 / 3** in three runs, and the affected/related scope passes **217 / 217** (`rtl-all-paths-related-20261110.trx`): subtitle hand-off, embedded caption preparation, playlist downloads, native short-media decode, temporary watch, season library, audio preference, temporary-watch discovery and proxy lifetime.

The on-screen effect of the marks was shown on October 24 with Sintel's Arabic captions (the period, the Arabic question mark and the ellipsis on the left). This pass adds no new on-screen run.

**Still not covered:** right-to-left captions inside other containers (MP4 text tracks, or HLS renditions with WebVTT segments that LibVLC reads itself), and ASS files, which keep libass's own layout.

## Fault 2: two tracks with the same label

Two saved captions in one language ("Arabic (downloaded)" twice), two provider tracks both labelled "English", two untitled embedded English tracks, or two files with no language all showed identical menu entries.

**Repair (accef36).** Only the entries whose labels collide now carry their position in the menu, for example "French (downloaded) · 1 of 2" and "French (downloaded) · 2 of 2", or "English · 1 of 2". Nothing is claimed about how they differ; an entry with its own description ("English · SDH") keeps its label. Keys and remembered choices are unchanged.

**Checks.** `SubtitleHandoffTests.CaptionsWithTheSameLabelAreNumberedSoTheyCanBeToldApart` (saved files, choosing "2 of 2" attaches the second file; two provider "English" tracks) and `EmbeddedTracksWithTheSameLanguageAreNumberedOnlyWhereTheirLabelsMatch` both failed before the repair (`same-label-before-repair-20261110.trx`) and pass after it. My October check that expected two identical "Downloaded subtitles" entries now expects them numbered. The caption/native/download scope passes **125 / 125** (`same-label-related-2-20261110.trx`).

## Subtitle-site adverts in provider captions (proposal only)

Nothing is stripped. This needs the user's decision.

**Measurement.** I scanned every caption file already on disk from earlier QA: 19 unique files, 8,190 cues.

- **All seven** Breaking Bad Season 1 captions (provider captions that Vidsrc took from OpenSubtitles) carry exactly **two advert cues** each:
  - one early cue, between 0:06 and 3:56, always 6.074 s long;
  - the last cue of the file, about 60 s long, after the last line of dialogue.
- They use four wordings:
  - "Watch Online Movies and Series for FREE / www.osdb.link/lm"
  - "Support us and become VIP member / to remove all ads from www.OpenSubtitles.org"
  - "Do you want subtitles for any video? / -=[ ai.OpenSubtitles.com ]=-"
  - "Advertise your product or brand here / contact www.OpenSubtitles.org today"
- The Sintel captions from the same provider (eleven languages, October 23) were removed with their temporary watch and were not scanned. catalog-dash-tv saw the same kind of cue first in The Office S01E01.
- Archive speech-recognition captions and the anime caption sample have none.

**What does not work.** Keyword matching on words like "visit", "advertise" or "support" caught real dialogue ("Well, you ought to visit lockup.", "...that performs as advertised.", "I'll pick the horn up when I visit him.") and speech-recognition text.

**Proposed rule.** Hide a cue only when its text names a subtitle-distribution site: `opensubtitles.org`, `opensubtitles.com`, `osdb.link`, `subscene.com`, `addic7ed.com`, `podnapisi.net`, `yifysubtitles.*` or `subdl.com`. On these 19 files it matches exactly the **14** advert cues and **no** dialogue. A general "contains a web address" rule would also catch the adverts, but a film can legitimately show or say a web address.

**Choices for the user:**
1. Hide matching cues in provider captions only (downloaded and streamed). Recommended.
2. Also hide them in local and torrent files.
3. Leave captions as they are.

Until the user decides, captions are shown unchanged.

## Still open in this item

- **Anime dual-audio episode:** spoken Dub and full caption selection, with automated speech sampling as supporting evidence. It was left for a later turn; the human listen stays with the user.
- **The advert rule** above, which awaits the user's decision.
- **Right-to-left captions in other containers, and ASS files**, as noted under fault 1.
- **The October 5 manual CC Off report** stays unexplained. The user does not remember which symptom it was; the direct CC button is the dependable path.
