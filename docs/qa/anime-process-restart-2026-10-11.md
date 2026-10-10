# Actual anime process restart — October 11, 2026

The user explicitly instructed the agent to handle launch and continuation while they slept. The same prepared `Launch-Isolated-Recovery.ps1` ran successfully, resolving the earlier manual launch handoff without an alternate launcher. The prior 9454fb0 launch receipt was preserved before the launcher wrote its new receipt.

The actual candidate is **a3c53944c35cf438366e8de933e65c97b6b4ae18 / PID 33320**, launched **2026-10-10 23:22:54 UTC** with the existing private QA profile. Its 1280×800 window is on the second monitor at (2880,478). Startup showed “Service checks passed”; all three deployed scripts match the published package. This candidate includes native Sub preference but excludes the later manual-audio and deployed-cache repairs.

Actual AniList search → original Frieren Details (**AniList 154587 / MAL 52991 / 28 episodes**) → Dub episode 3 → Stream episode → native Stream restored **Playing 09:02**, without seek input or website fallback. K paused the decoded frame at **09:11**. Episode 2's subsequent separate request failed; no episode-2 player opened. Returning to episode 3 retained its **Paused 09:11** frame and position.

Read-only canonical episode rows are **115.654 / 308.972 / 551.672 seconds** for episodes 1/2/3. Episode 2 remains unchanged, as do both unrelated rows with digest `18e2efa16928c473a08b300f0540648720ff7c9239412f0ebdae7aa14f79810d`. Expanded native options still truthfully show **“Dub server selected · audio language unverified”** with a disabled Track 2 choice and English captions. No new spoken-English or physical Japanese-track pass is claimed while the user is away. The app was left paused.

- [Actual restart receipt](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/anime-process-restart-live-20261011.json).
- [Original preserved launch](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/manual-recovery-launch-9454fb0-before-a3c5394.json), [a3c5394 launch](C:/Users/user/animeapp/.artifacts/implementation/package-smoke-20261009/manual-recovery-launch-a3c5394-before-217ab84.json).
- [Episode 2 failure and bounded repair](C:/Users/user/animeapp/docs/qa/anime-server-allocation-2026-10-11.md).

This qualifies the finite episode-3 actual process-restart/native-resume sample and independent failed adjacent request. Repaired episode 2, broader providers/languages, different website-provider identity and final packaged acceptance remain open. No grouped gate is closed; Books remain deferred and Arabic cartoons last.
