# Pressured new-source admission, real app — October 21, 2026

**Qualified (automated, real executable, second monitor).** This closes the "pressured new-source admission" part of the open shutdown/admission group. The October 17 snapshot-paced desktop sequence and its 20-second lock were not repeated. Branch **codex/app-recovery** on **a9f0ce2**; the AppFixture sandbox profile only, with no real profile, library or download.

## Journey (`PackagedSourceAdmissionTests`)

1. **Two library items.** The test generates a 10-minute film and a 10-minute episode (ffmpeg test patterns), each beside its own `library-item.json`. They are a Movie `av:fixture:film:pressure / feature` and a TV episode `av:fixture:show:pressure / season:1:episode:7`. The episode's row is seeded at **421.5 s**.
2. **The film plays and commits.** The real Player opens the film through "Media URL or local file path" and "Open media URL or path", so its catalog identity comes from the sidecar. Skip +30 s and Pause commit the film row at **31.700 s**. The test waits until the journal has acknowledged it, so nothing is outstanding.
3. **A real SQLite writer is held**, with a 60 s safety bound only; it is released explicitly once the waiting state has been checked.
4. **Pressure through the transport button.** While the first pause's commit holds the shared persistence gate, each Play queues one ownership operation and each Pause one save, so 2k − 1 operations wait after k cycles. The test reads the player's threshold constant (30) and runs 16 Play/Pause cycles. The 16th Play is still admitted at 29, and the source change then sees 31. Every Play is checked as admitted, and each step waits for the decoder's own Playing or Paused event. The app log confirms **16** admitted Plays and **18** Pauses (16 cycles, plus the film before the writer and the episode after admission). The cycles took **2.284 s**, 2 s of which was a deliberate first play.
5. **The episode request waits.** "Open media URL or path" with the episode's path is deferred:
   - "Saving progress before continuing..." shows in the status line and the video overlay for **1.069 s** with the writer held;
   - Play/Pause, Open and the source field are disabled;
   - the title and clock stay on the film at **33.801 s**;
   - the film's newest position is in the journal (33.801 s) while a private WAL reader still sees the film at 31.700 s and the episode at 421.5 s.
6. **Release and admission.** **0.149 s** after release the waiting state clears, controls re-enable, and the episode opens on its own row: the log shows "Loaded saved position 421.5s" for that unit. It plays from there and pauses at **423.500 s**.
7. **Separate rows.** The film row is **33.801 s** (the pressured progress, not 31.700) and the episode row **423.500 s**. The seeded fixture row stays **120.5 s**, and the journal empties. The log has no "Deferred playback request failed" and no "could not be journaled or saved".
8. **Normal close** through the window's own close request logged a full drain. Exactly three rows remain.

## Runs

| Run | Result | TRX |
| --- | --- | --- |
| First | Fail. Whole-window UI Automation searches took ~4 s per toggle, so the writer's 60 s bound expired mid-pressure and the first blocked commit timed out at 30 s (logged as journaled, not lost). Fixed by caching the player's elements once. | `packaged-source-admission-first-20261021.trx` |
| Second | Fail at a wrong test assumption: the notice appears three times (status line, busy overlay, waiting overlay), not twice. The waiting state itself was correct. | `packaged-source-admission-second-20261021.trx` |
| Third | Pass. Its episode check could still match the seeded row, so it was strengthened. | `packaged-source-admission-third-20261021.trx` |
| Final | **Pass, 14 s** | `packaged-source-admission-20261021.trx` |

Designing this case exposed a real defect: a failed journaled commit stranded deferred admission and zeroed the next read. It was repaired first in [a9f0ce2](failed-journaled-commit-2026-10-20.md). Build: zero warnings and errors.

## Hosted checkpoint failure and test repair (later on October 21)

The hosted checkpoint for **0c8f634** ([run 39614476383](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/39614476383)) finished **1,400 passed / 1 failed / 22 skipped of 1,423** in 13 min 1 s. The failure was this case: "The pressured source change did not wait." The runner's app log showed:

- all 16 Plays and 16 Pauses;
- no durable save after the pre-writer 31.7 s;
- the episode's Open admitted at once;
- its restore still waiting behind the writer, with no warning or error.

**Cause: the test's assumption, not the app.** A journaled commit applies its own journal revision, and a newer observation of the same unit replaces the older record. The runner's write-through journal writes took longer than one ~90 ms Play/Pause cycle. So the second pause was journaled before the first commit looked its revision up. That commit correctly found its record superseded and skipped SQLite, and no operation held the shared gate behind the writer. Each later commit was superseded the same way. Fewer than 30 operations were ever outstanding, so admission correctly let the source change through. This is the coalescing working as designed. Locally a journal write takes milliseconds, so the first commit always reached the writer first.

**Repair (test only).** After the first pressured pause, the test waits until the journal holds that position, then allows 1 s for the commit's single queued journal lookup. From then on the first commit is inside SQLite behind the writer, and every later operation queues behind the gate, whatever the storage speed.

**Reproduction.** A temporary scratchpad variant held the journal lock in 150 ms pulses for the whole pressure loop, so every journal write took longer than a cycle. The test file was restored afterwards with a matching hash.

| Run | Result | TRX |
| --- | --- | --- |
| Unchanged test at the current head | Pass, 14 s | `packaged-source-admission-head-20261021.trx` |
| One 800 ms lock hold (insufficient emulation: the loop finished before it ended) | Pass with the unchanged test, so discarded as evidence | `packaged-source-admission-slow-journal-before-20261021.trx` |
| Unchanged test, pulsed slow journal | **Fail**, with the runner's message and log signature: 16 Plays and 16 Pauses, no later durable save, the episode Open admitted at once, its restore waiting, no warnings | `packaged-source-admission-slow-journal-pulses-before-20261021.trx` |
| Repaired test, pulsed slow journal | **Pass**, 15 s | `packaged-source-admission-slow-journal-pulses-after-20261021.trx` |
| Repaired test, normal | **Pass**, 15 s | `packaged-source-admission-supersede-fix-20261021.trx` |

A first attempt at the pulsed variant did not compile (the xUnit analyzer rejects a blocking `Wait()`), so its two runs used the previous build. Those results were deleted and are not counted.

## Limits

- The evidence is FlaUI driving the actual executable on the second monitor, not a human observation.
- The repaired case is not yet confirmed on a hosted runner; the next checkpoint will show it.
- The shared 64-operation threshold across players is not exercised; that remains covered by the 20-player automated matrix.
- The case relies on the transport cycles finishing well before SQLite's 30 s command timeout (asserted under 25 s; 2.3 s locally before the added wait of about 1 s).
- Like the 50-second durability case, whether this 14-second case stays in the hosted suite is an open product decision.
- Real Windows shutdown and portable configuration migration remain open in the same checklist group.
