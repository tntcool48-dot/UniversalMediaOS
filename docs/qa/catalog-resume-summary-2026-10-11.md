# Catalog Details resume summary — October 11, 2026

Actual retained TV Details displayed an old S01E02 7:23 resume in the title overview while its Library panel correctly updated to S01E01 13:37 after native playback. The selected episode and durable rows were correct; the duplicate summary was stale.

My Library constructed its immutable metadata overview from the last-opened unit/position. Later LibraryChanged events updated the dedicated ResumeText binding but could not update that immutable snapshot. Library cards now use the existing media mapping without inserting mutable resume/status text into metadata. The dedicated Library panel remains the single progress summary; source catalog overviews, persisted work IDs, favorites/status and episode selection retain their existing behavior. No new data format or migration is introduced.

The reused Release WPF build passed with zero warnings/errors in eight seconds. This small presentation change adds no implementation-mirroring tests and repeats no passing automated scope. Actual updated-package Movie/TV Details qualification is pending; the earlier source 89c3e29 checkpoint is passing evidence for its own code, not this change.
