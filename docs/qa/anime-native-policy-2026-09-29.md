# Anime native streaming and explicitly chosen website fallback

**September 29, 2026. Scope:** D / IP11. This implements the website fallback policy for anime; recovery and full anime acceptance remain incomplete. Progress is recorded in the [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md).

## Problem and behavior

A browser-only result ended lookup at several levels: the first failed media capture, iframe, provider candidate and then the entire provider pool. The C# router opened that result as a website even when the user chose Stream. Consequently a protected or decoy stream on one host could hide usable native media on another host. The normal Stream action did not mean direct playback in the app.

Native resolution now saves one browser candidate per attempted episode page and continues other extraction stages, frames and selected-audio servers, then candidates and providers from the existing automatic indexes. A later validated native result takes precedence. Existing candidate/site/global limits remain. After exhaustion, a browser-only result communicates website availability; the native router rejects it and stops visibly. It never opens a saved manual website URL as a substitute after native failure either.

The existing website choice is now labeled **Open website**, with **Website — last resort** explanatory text. It explicitly requests website mode and can return the first eligible captured player page without waiting for native alternatives. Stream stays the default. The optional process argument defaults to native mode, including when omitted by an older caller; the service passes native/website mode according to the chosen action. Adjacent-episode routing retains the originally chosen mode. The existing native player's explicit browser recovery action and hidden site playback controls are preserved.

This introduces no provider picker, pinned provider, mandatory key, dependency, data migration or replacement download subsystem. Movie/TV website behavior is a separate remaining F item.

## Accuracy and limits

Title/season/part/unit conflict checks, navigation-failure protection, media-byte validation and requested Sub/Dub guards remain in place. Native source handoff retains captions, audio information and the validated advertised HLS variant. A failed or known Japanese-only Dub result cannot become website success. The first saved page keeps the observations from its own checked parent; a subsequent conflicting server rotation cannot overwrite that evidence.

A syntax-tree comparison also confirms that 13 matching/media helpers and both native/failed-Dub guard branches are unchanged from the before snapshot. These regressions protect the identified failure cases. They do not measure a population-wide accuracy percentage, complete independent source ID/alias mapping, or certify unknown evidence. Python identity observations still need the broader canonical evidence/persistence work already listed under IP07/IP08. A romanized-title probe for **Sousou no Frieren** did not resolve in its budget; main-provider search returned no accepted links and later candidates were rejected/exhausted. The English catalog title **Frieren: Beyond Journey's End** succeeded in the final run. This alias gap is explicitly retained, rather than weakening the existing title guard.

## Verification

The final production Debug build has **zero warnings/errors**. **493 selected C# cases and 95 Python cases passed**, with zero failures/skips; this adds eight C# cases and 14 Python cases. Coverage includes browser-only result rejection, explicit website selection, unchanged title/unit/audio request routing, known wrong Dub, one discovery pass, cancellation, later native candidates/providers/frames, original page evidence, and existing site/global limits. Existing matching, subtitle/audio, decoy payload and variant regressions still pass.

Eight desktop-input classes remain excluded: PlaybackPresentationTests, RetainedPlayerTabsTests, BootWindowTests, DownloadTests, MangaTests, NavigationTests, SearchTests and SettingsTests. No new physical UI/focus/fullscreen or packaged-app acceptance is claimed. The dialog wording is compiled; its layout still belongs to normal UI acceptance.

Fresh resolution uses the actual production **ScraperEngine**, **PythonBootstrapper**, copied build scripts and Python CLI, with an isolated data root. The unchanged 202-entry cached index snapshot was copied from the preceding matching check. The diagnostic's router then consumes the captured service result through **TripleNetHandoff** and the production proxy, separately from fresh lookup. This checks handoff policy; it does not start VLC, render a new frame, independently listen to audio or certify the next/previous UI. All browser workers are isolated/offscreen and no agent desktop keyboard/mouse/activation is used. After the diagnostics, no owned app/service/browser worker remained.

| Final production-service request | Seconds | Handoff result |
| --- | ---: | --- |
| Mushoku Season 3, episode 1 Sub | 19.882 | Native stream, validated variant |
| Frieren English catalog title, episode 1 Dub | 19.766 | Native stream, validated variant |
| Explicitly selected episode website | 20.836 | Captured episode website, explicitly selected |
| Caller cancellation after four seconds | 4.151 | Cancellation propagated; no website result |

Sub and English-title Dub results preserve their validated variant and route to Tier 1. Sub carries three caption files; Dub carries one. Dub is selected from the provider's Dub group but has no embedded audio-language tag; its unverified-language state is retained. The explicit website request resolves a captured source and routes to Tier 2, without native playback being counted as website acceptance. Individual timings depend on current network/provider behavior and are not a new speed benchmark against the previous Python-only measurements.

An earlier diagnostic on the same policy script found Sub in 19.226 seconds, failed the romanized Frieren title after 112.807 seconds, found the explicitly chosen Sub website source in 19.126 seconds, and propagated four-second caller cancellation in 4.149 seconds. The final diagnostic uses the final production assemblies and English catalog title; earlier failures are not replaced or counted as successes.

## Remaining and artifacts

Finish aliases/independent provider IDs, unknown/special unit mapping, broader native first-frame and outage/cancellation behavior, next/previous UI, returned-frame/focus/resource and changed-provider/restart resume, and remaining A/B/C acceptance before the agreed temporary download, Movie/TV, book and Arabic-cartoon work. Seven user-facing work areas remain when shared performance/service/release checks are grouped together; the 39 unchecked checklist entries include overlapping acceptance requirements.

[Production executable](C:/Users/user/animeapp/.artifacts/implementation/anime-native-policy-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [final selected C# results](C:/Users/user/animeapp/.artifacts/implementation/anime-native-policy-20260929/results/anime-native-policy-final.trx), [Python results](C:/Users/user/animeapp/.artifacts/implementation/anime-native-policy-20260929/python-verified.log), [final service summaries](C:/Users/user/animeapp/.artifacts/implementation/anime-native-policy-20260929/final-summary.json), [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/anime-native-policy-checkpoint.json). Before snapshots, scoped diffs, logs, isolated profiles and the private diagnostic driver/runtime remain in the ignored artifact directory. Signed source/request payloads stay private.
