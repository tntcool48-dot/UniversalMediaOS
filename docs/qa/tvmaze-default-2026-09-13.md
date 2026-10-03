# Keyless Television catalog and episode selection

September 13, 2026. IP05/IP06 partial provider decision and series UI checkpoint.

TV browsing, search and episode metadata now use TVmaze without a user key. A saved TMDB key, Archive toggle or provider outage cannot silently reroute TV discovery to TMDB. Movie and Cartoon discovery retain their transitional routing pending a qualified film strategy. Saved TVmaze IDs own episode lookup; saved legacy IDs remain intact without guessed conversions. Per-service capabilities reflect the selected catalog, including unsupported locale overrides.

Series details load named seasons/episodes before source lookup. Users select an episode and choose Find sources. Changing the season clears the episode and any resolved sources. Replaced/closed/disposed details reject late successes and failures. Unknown specials preserve their provider ID and null playback unit; lookup does not substitute a feature or episode one. Unavailable/unsupported metadata exposes manual numbers and retry. A successful retry clears earlier results and restores the picker. Existing feature-source behavior is unchanged in this checkpoint.

Catalog/details attribution links use the TVmaze domain and a validated numeric show ID. HTML summaries render as plain text, ratings are bounded to the upstream 0–10 scale, and absent fields stay unknown. Settings describes the keyless TV provider separately from the remaining optional movie/cartoon key. No original audio, dub or subtitle claim is synthesized.

Twenty added deterministic cases cover four legacy-setting combinations; config-free defaults/cache continuation; saved Cartoon-series IDs; unsupported legacy IDs; upstream failures; null/valid display metadata; numbered episode forwarding; stale/special rejection; cancellation; fallback/retry; attribution; and two view rendering/binding cases. The renderer embeds the build's view XAML and uses production theme resources without starting background services. The view's code-behind only initializes its XAML. Rendered season/episode selectors update the real view model and remain within the viewport at 1280 dark and 900 light. PNGs were visually inspected; this does not establish live keyboard/popups, monitor/DPI transitions or full desktop acceptance.

Final exact-build suite: **410 passed, 22 existing skips, zero failures**. Build passed with zero warnings/errors. Earlier passes in this batch exposed an outdated empty-index fixture and a missing assembly qualifier in the isolated rendering harness; both were corrected. Neither result was treated as a successful full run. The final run used the exact assembly set from the successful isolated build.

The bounded TVmaze sample passed twice, including six searches, two browse pages with 240/245 shows and complete poster coverage, and 203 Office episodes. Request times were about 0.22–0.59 seconds on this host. The reproducible probe has a 12-second timeout and 4 MB read cap per request. Cinemeta movie results contained TMDB-related fields; this candidate is not enabled as a non-TMDB film replacement. See [the provider decision](C:/Users/user/animeapp/docs/decisions/keyless-metadata.md) for sources, observations and remaining qualification gates.

Evidence locations:

- `.artifacts/implementation/results/tvmaze-default-e2e.trx` and `.artifacts/implementation/tvmaze-default-checkpoint.json`.
- `.artifacts/implementation/tvmaze-default-build/bin/UniversalMediaOS.Tests.E2E/debug/ui-evidence/tv-episodes-1280-dark.png` and `tv-episodes-900-light.png`.
- `.artifacts/implementation/catalog-selection-20260913/`, including the repeated sample under `repeat/`.
- Pre-edit files: `.artifacts/implementation/tvmaze-default-before/`.

Remaining: useful non-TMDB film/animated-film discovery, combined Cartoon catalog, exact special/alternate-order playback, verified producer evidence, sustained availability, real playback and fresh/migrated-profile desktop journeys. These tests do not establish that live movies or series streams work.

```powershell
python tools/qualify-tvmaze-default.py --output .artifacts/implementation/catalog-selection-20260913/repeat
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/tvmaze-default-build
```
