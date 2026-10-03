# Audiovisual source selection lifetime

September 13, 2026. Partial IP04/IP07 implementation.

The previous catalog view model retained source buttons after the user changed a season, episode, language or Arabic-only selection. Playback combined the currently selected card with the clicked source. An obsolete provider exception could also clear a newer title's results because the failure path did not check operation ownership.

The view model now versions source selections. Changes cancel pending lookup, clear source results and require refresh. Lookup completion and failure may publish only for the current selection and request. Repeated lookup clears the prior list before awaiting providers. Play, external-open and download commands reject objects that are no longer in the current resolved source list. Playback and external-open recheck after asynchronous network validation. Already started downloads retain their captured item and source.

Closing details or disposing invalidates the source list. A delayed library-state read cannot update a different selected card, and a superseded detail-opening operation cannot start a lookup for its replacement.

Regression coverage includes five selection changes, obsolete success and failure after a new title completes, closing/reopening details, current-source validation, replaced source objects, and disposal. Private fixture URLs make unintended stale actions observable through the safety-error dialog without external network requests.

This is selection-lifetime protection, not independent verification that a provider supplied the correct movie or episode. Full frozen playback context, independently produced source evidence, exact special mapping, persisted resume migration and live playback acceptance remain open. The final keyless non-TMDB provider selection is unchanged.

The source file contained isolated legacy-encoded middle-dot bytes; it was normalized to valid UTF-8 while preserving those characters. The original bytes are preserved in the local checkpoint backup.

Build and test:

```powershell
.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/source-selection-build
```

Final verification: 366 passed, 22 existing skips, zero failures; zero build warnings/errors. Evidence: .artifacts/implementation/results/source-selection-e2e.trx and .artifacts/implementation/source-selection-checkpoint.json.
