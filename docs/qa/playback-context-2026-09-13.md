# Audiovisual playback context

September 13, 2026. IP04 contract and forwarding checkpoint.

Catalog cards now retain the work key created when they are constructed. Playback captures that key, identity, unit, title/poster, provider ID, source identity and available evidence in an immutable context. Identity aliases, alternate titles and audio/subtitle evidence lists are defensively copied into read-only collections. Unknown identities keep their existing per-card opaque key rather than generating a new key at every play action.

The optional context is appended to the existing playback message and load-method signatures. MainViewModel forwards it to native and embedded playback. Retry, browser fallback and quality changes retain it. An unrelated load without a context clears it. Existing anime callers remain compatible.

Unit keys distinguish features and numbered series episodes. Unknown forms, unnumbered series entries and specials without independent numbering evidence have a null unit key. This explicitly leaves their mapping unresolved; it does not turn them into a feature or invent episode one. Source evidence is retained as supplied, not promoted to verified by the context constructor.

Transport location, cookies and request headers are not part of this identity context. They continue through existing transport fields. A URL or display-title change does not change the frozen work key. The context is not yet used to migrate persistence or replace the legacy resume key.

Seven added test cases cover producer-list mutation, read-only snapshots, special numbering evidence, unknown units, URL/credential separation, optional-message compatibility, retry, native loading, browser fallback and unrelated-load reset. Quality-change and MainViewModel forwarding are compiled and inspected; real native/browser playback remains separate acceptance.

Remaining: IP07 independent evidence extraction and matching, episode-provider identity support for unnumbered specials, IP08 persisted work/unit migration and resume adoption, and actual playback journeys. Final non-TMDB keyless metadata selection remains open.

Reproduce with:

```powershell
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/playback-context-build
```

Final verification: 373 passed, 22 existing skips, zero failures; zero build warnings/errors. Exact-build evidence and hashes are retained in the implementation artifacts.
