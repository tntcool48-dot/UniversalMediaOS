# Retained Archive identity and resume dispatch — October 16, 2026

Two concrete faults are repaired in one provider/reliability batch. The retained before run has **13 failures /zero passes** (12 Archive cases and one resume case). The final combined affected/risk scope passes **51 /zero failures/skips**, reported test duration **nine seconds**, reusing Release outputs. It includes all ten anime progress cases, a real STA stale-callback case, and real SQLite ownership/backpressure admission. Original TRXs remain under `.artifacts/implementation/package-smoke-20261009/identity-resume-20261016`. No broad local suite, repeated passing cohort or deadline increase ran.

## Resume read completed, application never ran

A fresh owned MTA worker explicitly creates a WPF dispatcher but never pumps it. With no Application or synchronization context, a real canonical anime/episode row at **137.25 seconds** is read successfully. Before repair the database task is RanToCompletion while ResumeLoadCompleted remains WaitingForActivation and pending reads are zero. The existing five-second application deadline fails. Only cleanup subsequently pumps the queue and restores/removes the private profile; that pump cannot satisfy the assertion.

Playback dispatcher capture now prefers the real application dispatcher, or a dispatcher on an STA UI host. An incidental MTA dispatcher is ignored. The same helper serves construction, ordinary resume loads and deferred admission; explicit target-dispatcher precedence and generation/order/position guards remain. The regression now applies and returns exactly 137.25 without pumping the worker. The related STA and writer-pressure cases retain real UI dispatch, stale-unit rejection and the final accepted position.

The earlier d71ef78 hosted run's five AnimePlaybackProgressTests failures were resume-load timeouts. Its original results supplied no phase output or worker IDs. Image decoding can create an incidental MTA dispatcher, and timing in that run is consistent with worker reuse, but the exact reused thread is **unproven**. This deterministic completed-read/blocked-application failure establishes a real fault without relabeling the original run flaky. Later source 4d8bba9 passed all hosted cases before this repair, so that successful run is not presented as validation of the new guard.

## Resolve the retained Archive item

Saved Archive IDs previously re-entered title/year search. A missing year yielded no source; a same-title/year search could return another Archive item. A single retained `internetarchive/item` identifier now requests its own metadata endpoint directly, including when the saved display title/year is absent. Conflicting saved IDs stop before HTTP; missing/bad metadata never falls back to title search.

The returned metadata must independently supply an exactly matching, case-sensitive string identifier. Observed title, year, form and unit come from the response. A missing year is allowed only for this independently bound item; ordinary title-search requirements remain. Exact evidence matching, explicit year/ID conflicts, animation/form, season/episode and file guards still apply. Matching IDs cannot turn trailers or episode files into a feature or substitute an adjacent TV unit. Audio remains unknown.

The final direct-ID class contains **13 cases**, including a later blank-title case. Fixtures verify one exact case-preserved endpoint, PrimaryId/ExternalIds, known/missing year, independent evidence, missing/wrong/case-mismatched identifiers, conflicting IDs, cancellation, missing metadata and rejected files/units. They are synthetic HTTP responses, not live Archive or UI captures.

## Limits and next acceptance

This batch does not close the broad provider or shared-reliability groups. Live Archive availability, catalog-bound DASH, exact cuts, spoken audio, full captions, sustained playback, pressured source replacement, OS shutdown and final package/release acceptance remain open. No original user/profile data or existing downloads were used by these regressions. Books remain deferred; Arabic cartoons remain last.
