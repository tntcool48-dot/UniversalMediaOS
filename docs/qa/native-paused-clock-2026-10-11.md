# Native paused-clock consistency — October 11, 2026

The actual 6375d52 packaged Sintel stream showed 185.146 seconds while its pause callback saved 188.083 seconds, a 2.937-second discrepancy. That physical sample remains evidence for the earlier executable; the source repair below awaits updated-package physical qualification.

The native pause callback saved the live LibVLC clock but left the visible clock at its last progress notification. It now takes one final position snapshot, updates the display under the existing player-update guard, and uses that snapshot for persistence and peer pause publication. The guard prevents a display refresh from becoming a user seek. Existing buffering-failure, persistence-waiting, completion and unit-ownership guards remain.

A real generated-video decoder regression models sparse progress notifications while native frames and time continue advancing. Before repair it failed with a 2,499 ms visible/native difference. After repair it confirms the final paused display and durable position agree to 1 ms, playback stays paused, no backward seek occurs, and an adjacent resume row stays at 57 seconds. This is a controlled notification-gap sample, not a claim about the frequency or cause of every provider's sparse events.

The regression plus locked-resume startup, paused HLS frame seeking and both playing/paused buffering-timeout cases pass: **five passes, zero failures/skips, 13 seconds**. The reused Release build completed successfully. No broad local suite ran. Results: [before](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-before.trx), [after](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-after.trx).

The live private app/profile was left intact. The clock and browser-cookie repairs are prepared as one coherent source batch; the current executable remains 6375d52. Its next coherent package needs physical pause/display/durable-position qualification. Native TV restart still needs a verified available source. The count remains 108 checked steps / 17 open groups, including two deferred Books groups.
