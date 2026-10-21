# Accessible names across the main views — October 21, 2026

Extends the [Settings](settings-accessible-names-2026-10-21.md) and [anime search](search-paging-filters-order-and-names-2026-10-21.md) audits to every main view. Branch **codex/app-recovery** after **d87c4e2**, in its own AppFixture profile on the second monitor.

## Audit

`AccessibleNameAuditTests.EveryMainViewNamesItsInteractiveControls` opens Anime, Manga, Movies, Books, TV Shows, Cartoons, Player, VA Detect, Watch Together, My List and Downloads in turn. In each, it lists every visible, keyboard-focusable button, field, combo box, check box, slider, radio button and link whose UI Automation name is empty. A screen reader announces such a control only by its type.

Before the repair it reported **9** unnamed text fields (`app-accessibility-before-20261021.trx`). Its message then merged identical lines, which showed them as seven; it now counts duplicates.

| View | Field | Name now |
| --- | --- | --- |
| Manga | search box (placeholder "Search MangaDex titles...") | Search MangaDex titles |
| VA Detect | anime title search | Anime title for VA matching |
| VA Detect | manual cast page URL (placeholder "Manual cast URL") | Manual cast URL |
| VA Detect | MAL username for the public fallback | MAL username for public fallback |
| Watch Together | relay port (label "Port" under Local Relay) | Relay port |
| Watch Together | server (label "Server") | Server |
| Watch Together | room ID (label "Room ID") | Room ID |
| Watch Together | offset (label "Offset seconds") | Offset seconds |
| Watch Together | host URL beside "Share URL" | URL to share |

Each name repeats the visible label or placeholder; these views' labels are English, so the names are too.

## Verification

- The audit passes after the repair (`app-accessibility-after-20261021.trx`).
- With the Manga, voice-cast UI, navigation and live Watch Together playback tests that drive these views, **10 / 10** pass in 1 min 37 s (`app-accessibility-related-20261021.trx`). Those tests locate fields by type and order, which the names do not change.

## Limits

- Names only. Narrator or NVDA speech, focus order and Arabic speech were not checked.
- Dialogs and windows outside the main views (for example the source picker) were not audited.
- Views were audited in their initial state; controls that appear only after an action (for example inside opened Details pages or a playing player overlay) are not covered.
