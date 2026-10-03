# Anime catalog-ID conflict check

**September 30, 2026. Scope:** D / IP07/IP11. This is an optional independent-ID guard, not completion of the anime watchability gate. Progress is tracked in the [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md).

## Change

AniList media already supplies an AniList ID and sometimes a MAL ID. Before this batch, the resolver received only title aliases and episode number, so it could not reject a provider page that explicitly identified another anime. The app now sends those existing IDs through normal Stream, the separately chosen website action and adjacent-episode resolution. No new user key or provider selection is needed.

The Python resolver reads explicit IDs on the current page's active episode, primary heading/head metadata or head canonical/OG URL. Arbitrary recommendation links, request parameters and IDs copied into a generated URL do not count. An observed different ID in the same namespace rejects the page before any initial network stream or extraction stage is accepted. A matching ID is recorded as evidence but does not bypass the requested title, season, part, form or episode checks. If a provider exposes no recognized ID, the existing title/unit rules apply unchanged. The resolver makes no additional site request and keeps its prior budgets.

## Verification and limit

Production Debug build: **zero warnings and errors**. **495 selected C# tests and 113 Python tests passed**, zero failures/skips. Eight desktop-input C# classes were excluded while the user uses the primary monitor. New cases cover both playback choices' ID forwarding, matching and mismatching source IDs, absent and invalid IDs, restricted external-ID URLs, rejection despite an otherwise matching title, preservation of title/unit conflicts despite matching ID, and rejection before stream capture. The actual page JavaScript passed `node --check`. No desktop input or activation was used.

There was no new live provider-page or player-frame run in this batch. A real source may omit these IDs; this implementation protects pages that expose them but does not yet measure provider coverage or prove watchability. It changes no search latency mechanism or title-match threshold, so no new speed or accuracy rate is claimed. Missing aliases, unknown/special units, wider provider/outage, resume and A/B/C/D acceptance remain open.

[Built executable](C:/Users/user/animeapp/.artifacts/implementation/anime-provider-ids-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe), [selected C# TRX](C:/Users/user/animeapp/.artifacts/implementation/anime-provider-ids-build/results/anime-provider-ids-selected.trx), [Python log](C:/Users/user/animeapp/.artifacts/implementation/anime-provider-ids-20260930/python-final.log), [JavaScript syntax artifact](C:/Users/user/animeapp/.artifacts/implementation/anime-provider-ids-20260930/page-identity-syntax.js).
