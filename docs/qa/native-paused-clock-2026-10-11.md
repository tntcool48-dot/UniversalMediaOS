# Native paused-clock consistency — October 11, 2026

The actual 6375d52 packaged Sintel stream showed 185.146 seconds while its pause callback saved 188.083 seconds, a 2.937-second discrepancy. That physical sample remains evidence for the earlier executable; the source repair below awaits updated-package physical qualification.

The native pause callback saved the live LibVLC clock but left the visible clock at its last progress notification. It now takes one final position snapshot, updates the display under the existing player-update guard, and uses that snapshot for persistence and peer pause publication. The guard prevents a display refresh from becoming a user seek. Existing buffering-failure, persistence-waiting, completion and unit-ownership guards remain.

A real generated-video decoder regression models sparse progress notifications while native frames and time continue advancing. Before repair it failed with a 2,499 ms visible/native difference. After repair it confirms the final paused display and durable position agree to 1 ms, playback stays paused, no backward seek occurs, and an adjacent resume row stays at 57 seconds. This is a controlled notification-gap sample, not a claim about the frequency or cause of every provider's sparse events.

The regression plus locked-resume startup, paused HLS frame seeking and both playing/paused buffering-timeout cases pass: **five passes, zero failures/skips, 13 seconds**. The reused Release build completed successfully. No broad local suite ran. Results: [before](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-before.trx), [after](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-after.trx).

The live private app/profile was left intact. The clock and browser-cookie repairs are prepared as one coherent source batch; the current executable remains 6375d52. Its next coherent package needs physical pause/display/durable-position qualification. Native TV restart still needs a verified available source. The count remains 108 checked steps / 17 open groups, including two deferred Books groups.

## Actual first-package return and follow-up

Source 567cb7e / PID 65228 restored Sintel at 188.083 seconds and rendered changing native frames. K paused it at displayed/saved 192.847 seconds. A later tab-leave save observed 193.115 seconds while the returned player still displayed 192.847, exposing a remaining 268 ms difference. The Archive transport buffered slowly; its immediate late Buffering 64% overlay subsequently cleared to Paused on return. This sample does not qualify sustained streaming speed.

The same process resolved independently matched original Avatar S1E2 through the actual catalog, rendered native 4:3 animation, restored its separate 418.197 seconds, and paused at displayed/saved 443.842 seconds. S1E1 remained 747.957 seconds; the ten other rows and configuration stayed unchanged. No spoken-audio or full-caption claim is added.

A simple delayed local pause did not reproduce the later clock drift. The regression was strengthened with a real paused native seek while progress notifications remain sparse; tab-leave then failed with a 2,000 ms display/save difference. The follow-up synchronizes the display to the snapshot used by forced tab/source/close saves, using the existing no-seek guard. Playback errors and persistence waiting retain their displayed clocks. The extended case plus the same four related cases pass: five passes, zero failures/skips, 13 seconds. [Return failure](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-return-before.trx), [follow-up](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-pause-clock-return-after.trx).

The still-running 567cb7e full checkpoint was cancelled as superseded; it is not a passing result. Only the final source will receive the completed full checkpoint. Follow-up packaged pause/tab-return qualification remains pending.
