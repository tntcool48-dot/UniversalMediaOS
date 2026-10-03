# Movie/TV item evidence and caption handoff — October 1, 2026

The automatic scraper now carries the selected player's independently returned item metadata and reachable timed caption files into the native player. The final Dune (2021) sample passed identity verification, rendered English captions into a decoded native frame, sought, paused when inactive, stayed paused on return and resumed on command. Breaking Bad S1E1 returned independently verified episode metadata and passed fresh Python caption extraction, but the final native TV runs returned no captions. TV caption reliability remains open.

## Problem and repair

The embed request identifies what the app wants; it does not prove what the provider serves. Previously, the extracted Movie/TV stream carried neither independently observed film/episode metadata nor the player site's caption files. A verified title could also lose its warning about unknown audio language.

The scraper reads the loaded player's existing decoded item response, binding its release filename to the active player item and its stream URL to the current media. Content-bearing URL fields must agree; only the provider's appended access token may differ. Returned IMDb, title/year and release-file episode markers supply item-origin evidence. Contradictory response/file years, IMDb IDs, media kinds or episode numbers reject the candidate. Requested configuration IDs never certify a source. An ancestor's flattened media result cannot survive an identified conflicting child result.

The C# provider checks actual evidence before cancelling alternatives for a native success. Stream evidence takes precedence over search-result metadata. Encoded three-letter audio tags such as `eng` can match a requested two-letter tag such as `en`; captions never establish audio language. The player keeps **Audio language unverified** when no independent audio tags exist, even when film/episode identity is verified.

For the observed custom player, subtitle lookup normally starts after its browser video becomes ready. Native extraction can succeed while the browser's chosen HLS quality fails. The scraper can therefore start the provider's existing caption loader once for the bound release, then wait within a three-second caption budget and the unchanged overall extraction deadline. It stops if the active release changes. Only reachable files containing timed cues are handed off, with their own request context and provider-declared language. English/Arabic language labels are normalized for named choices. The source model, stream JSON and Movie/TV Stream and Watch via download messages carry those captions into the existing native caption pipeline. No new key, metadata service or provider picker was added.

## Verification

- **74 focused C# cases passed.** Five added cases cover rejecting a fast conflicting source while retaining alternatives, actual evidence/immutable caption handoff, ISO language matching, verified-identity/unknown-audio notices and the catalog's native caption message. Existing CLI parsing coverage now includes evidence and captions.
- **143 Python cases passed**, including ten new cases for response/file evidence, numeric movie titles, episode separators, strict stream binding, request-echo rejection, conflicting server items, real cue validation, late caption readiness and rejection of an ancestor fallback after child conflict.
- Full C# run: **564 passed, one failed, 22 existing skips, 587 total**. `SearchTests.T2_Search_05_Rapid_Concurrent_Double_Searches` failed at `SearchTests.cs:218` because its results-list UI lookup returned null. The cause has not been established; this run is not green. Movie/TV evidence and caption cases passed. Results: `UniversalMediaOS.Tests.E2E/TestResults/movie-tv-item-captions-2026-10-01.trx`.
- WPF and native helper builds completed with zero warnings/errors.

The native helper used the production Python bootstrapper, audiovisual provider, exact matcher, proxy and `PlaybackViewModel` under a WPF dispatcher. It decoded into memory, muted audio and opened no window.

| Sample | Lookup / observation | Established |
| --- | --- | --- |
| Dune (2021), `tt1160419`, feature | Final production lookup **20.454 s**; 87 decoded frames; duration 8,936.219 s; paused at 63.801 s, resumed to 64.403 s | Actual provider IMDb/title/year/release-file evidence verified. One English caption track loaded and selected in VLC; English glyphs visible in the captured native frame. Seek, inactive pause, paused return and explicit resume passed. One audio track found, with no language tag. |
| Breaking Bad, `tt0903747`, S1E1 | Fresh Python extraction **19.140 s**; another timed-caption extraction **16.375 s** | Provider-returned series/IMDb/year and release S1E1 agreed. Reachable English timed caption file passed. Final production native probes verified item metadata but returned no timed captions and stopped before native-caption assertions; the previous native video/seek/pause/resume checkpoint remains dated evidence. |

The final Dune image is `.artifacts/implementation/movie-tv-evidence-20261001/tt1160419-caption.png`. Its visible cue establishes native rendering for that sample, not full-film synchronization or authored-caption accuracy. Earlier memory frames had no visible cue; the final capture sought into dialogue before pausing. The final combined helper exited nonzero on the missing TV caption. No combined successful TV-caption result is claimed.

An earlier helper fetched a caption a second time after VLC had already loaded it and received HTTP 429. That redundant test fetch was removed. Live caption extraction remained intermittent; these observations do not prove the cause of every missing TV caption, and no larger timeout was introduced without evidence. VLC also logged memory-output converter/on-top and subtitle-option diagnostics; no unrelated production player repair was made for those helper logs.

Desktop QA could not start: the computer-use runtime repeatedly failed before execution with `failed to write kernel assets: The system cannot find the path specified. (os error 3)`, including after its reset/retry procedure. No window/input operation was performed through that tool. Actual catalog-to-player, fullscreen and tab interaction on the second monitor remain unverified for this build.

Source, WPF-output and helper-output audiovisual scripts matched SHA-256 `4A81BDE2A3B703F3A896509029AB21F476F5F4375ADC1B92211CEC2A4273DA3A`. Tested WPF DLL: `6196BB2C9E2876EBD64791ADECC4BAC22C8502FEEF84056685CCB8DA93DAC7D3`. Python results and local diagnostic helpers are under `.artifacts/implementation/movie-tv-evidence-20261001/`. Raw captures contain expiring provider URLs and are not embedded here.

## Still open

Stabilize TV caption delivery through the production native path and verify the actual secondary-monitor catalog-to-player journey. Check subtitle timing/selection across episodes, actual spoken language, correct remakes/cuts, two TV seasons/adjacent episodes/specials, and close/restart/provider-change resume. Other provider evidence, DASH segment validation, HLS/DASH downloads and season-wide TV downloads remain open. Investigate the failed rapid-search UI test before claiming a green regression run. Wikidata/TVmaze remain the keyless defaults; Books and Arabic cartoons remain later work.
