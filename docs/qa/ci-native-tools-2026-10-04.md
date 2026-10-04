# Native media test tools on GitHub Actions — October 4, 2026

The initial main-branch [validation run](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37112066928) restored and built successfully, then reported **722 passes, five failures and 22 skips**. All five failures attempted to launch `ffmpeg`, which was absent from the hosted runner. They covered real finite-DASH assembly/decode and two-player HLS/DASH proxy decode.

The workflow now installs [FFmpeg 7.1.1](https://community.chocolatey.org/packages/ffmpeg/7.1.1) before tests and requires both `ffmpeg -version` and `ffprobe -version` to succeed on PATH. Install/tool failures fail the job. The native tests remain enabled. GitHub's [Windows runner inventory](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md) provides Chocolatey for this setup.

Commit `2634f8d` was pushed normally to main. Its actual [successful validation run](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37166947839) installed FFmpeg/ffprobe 7.1.1, restored/built, passed **727 C# tests with zero failures and 22 existing skips**, and completed Python syntax validation. This result predates the season-source failover batch. Full Python behavior tests and packaged/physical application acceptance are separate gates; the active checklist remains open.

[Local retained logs](C:/Users/user/animeapp/.artifacts/implementation/ci-failure-20261004/) contain the failed and successful runner output. No local media tools, user profile or download was replaced by this workflow change.

The subsequent season-failover commit `009c131c` also passed its actual [hosted validation run](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37168079384): **743 C# passes, zero failures, 22 existing skips, 765 total**, 2 minutes 3 seconds; build and Python syntax validation succeeded. This confirms that batch on the hosted runner, not the later book changes or packaged/physical acceptance.
