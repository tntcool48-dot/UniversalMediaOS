# Archive short-film evidence — October 11, 2026

## Actual problem

Actual second-monitor package **dae593f / PID 76508** found Sintel (2010), its poster and short-film details through keyless Wikidata. The normal Find sources flow returned **zero verified / five unverified candidates**, including two Archive native options. Spoken audio remained unverified. No stream or download was opened for this pre-repair observation.

An independent bounded Archive metadata read found the exact 2010 `sintel_202207` item declaring **Blender, Sintel, Movie, Film, Short, Fantasy** in its own structured subjects, with an 888.08-second, 88,506,270-byte MP4. The adapter only recognized feature/full-length film declarations, so it omitted independent work-form evidence for this standalone short. The other exact `Sintel` item supplied fantasy/dragons/blender subjects without a film-form declaration; that candidate remains unverified. Four item metadata responses were inspected; no media bytes were downloaded by that probe. [Original metadata](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/sintel-archive-metadata-20261011.json).

## Repair and focused verification

The standalone feature unit already represents catalog movies, including shorts. The Archive adapter now recognizes an independently declared short-film phrase, or distinct Short plus Film/Movie structured tags. It uses only item/search subject, genre and type fields. Bare movies/Short categories do not establish film form. Supplemental trailer/clip/teaser/preview/excerpt declarations across either item or search keep the candidate unverified, so positive search tags cannot override a contradictory item. Exact title/year/IDs/unit checks and supplemental/wrong-unit filename rejection remain; audio is not inferred from catalog or item language.

Three positive short-form regressions failed before repair; four negative cases passed. [Original before result](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/archive-short-before-20261011.trx). The final **25 affected/related cases pass / zero failures/skips in eight seconds**, with a clean reused Release build. This includes the nine new short/contradictory-item cases, existing filename/unit protection and three Archive service/evidence cases. [Final related result](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/archive-short-related-20261011.trx). No broad local suite or unrelated passing scope was repeated.

## Remaining acceptance

The still-running dae593f package excludes this repair. Updated-source actual native streaming/download, temporary ownership, full-cut/duration, spoken audio and timed captions remain unqualified. Catalog DASH and broader providers/remakes/availability remain open. Source publication, its single hosted checkpoint and new package qualification will be recorded separately. Original profiles/media and the six unrelated untracked paths remain untouched.
