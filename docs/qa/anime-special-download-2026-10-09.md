# Catalog specials through Watch via download — October 9, 2026

The normal anime temporary-download path rejected every OVA/special-labelled release, including the selected catalog special. Its separate filename parser could also truncate fractional episode units, and treated the final dot in an extensionless release title as a file extension. Source inspection identified these faults before the real journey.

AniList's live read for **141534 / MAL 50360** confirmed the Eris work as **SPECIAL, one episode**, with its English/Romaji titles and explicit catalog synonyms. Catalog search now retains `format`; Details passes that form, episode count and trusted synonyms to the normal download service. Only a catalog-confirmed single SPECIAL/OVA at its first unit can accept an unnumbered exact-work release. The torrent metadata and selected video must independently match a trusted catalog title; multiple matching files remain ambiguous. Ordinary series keep numbered-file selection. OVA ordinals, batches, wrong works, fractional/ranged units and missing requested Dub evidence remain rejected. Numbered revision labels such as `01v2` retain their integer unit. No fractional-to-integer mapping or spoken-audio claim was introduced.

One related scope passed **46 checks / zero failures/skips in 82 milliseconds**, covering discovery, requested audio, exact special titles/aliases, missing/wrong catalog form/count, wrong units/works and existing discovery fallbacks. This includes **29 new cases**. Core, WPF and tests built with zero warnings/errors after correcting one nullability compile error. Existing publish and test locations were reused. The final null-safe synonym fallback changes only missing aliases; catalog data has a list. No passed Python scope or broad local suite was repeated.

## Actual second-monitor result

The working candidate used the existing isolated fresh profile; its main window was at **2880,478**, **1280×800**. Normal search returned seven Mushoku Tensei entries. The selected Eris Details retained **AniList 141534 / MAL 50360 / Episode 1 of 1**, Sub selected and the truthful Unknown Dub badge.

Clicking **Watch via download** ran the production RSS discovery. Nyaa returned zero results for the catalog-title queries; the AnimeTosho feed request failed with **No such host is known (feed.animetosho.org)**. Details retained the requested entry, showed the error and reenabled watch actions. No player or temporary job directory was created. Normal app close exited owned PID **23004**; held Frieren PID **33544** remained alive. The repo and deployed script SHA-256 matched **1DE08AD94E157B2CC338FA1CDF34BE9A25BB7AC92784B9B0A3BBFB8FBD209F6B**.

The accessibility set-value operation timed out after applying the query; a fresh screenshot confirmed it before Search. No duplicate query input was sent. This is a tooling observation, not an application failure. Private launch/result/TRX receipts remain in `.artifacts/implementation/package-smoke-20261009/`.

## Limits and next action

The actual attempt proves retained identity and a truthful discovery failure; it supplies no playable special, completed transfer, decoded frame/audio/caption or real provider-unit reconciliation. The exact-work matching/selection changes have controlled evidence; their positive transfer remains open. Earlier `e88da4b` full validation predates this source batch. The candidate is explicitly marked unsealed until its source publication and manifest are refreshed; the preceding manifest was preserved privately.

Continue with a verified special source through the normal native/download paths, preserving work/unit mapping and final-owner cleanup. Do not repeat the failed availability attempt merely to seek a different outcome. The tracker has **76 checked steps / 17 open groups**, including two deferred Books groups.
