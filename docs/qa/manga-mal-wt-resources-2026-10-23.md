# Remote Watch Together relay, Manga reading progress, live notifications — October 23, 2026

Checklist item: "Qualify live Manga progress, real-account MAL, Windows notifications, remote Watch Together media/audio and remaining resource retention." This batch repairs three demonstrated user problems and prepares the two steps that need the user. Branch `claude/manga-mal-wt-resources-b261b9`, cut from `e744dcd`.

## 1. Remote peers could not join an ordinary user's relay

**Problem.** The relay listened through `HttpListener`. Its first two prefixes (`http://+:port/`, `http://*:port/`) need an administrator URL reservation. A normal, non-elevated user (this machine's user is not elevated) fell back to `http://localhost:port/`, and http.sys then answered every peer that used this machine's LAN or Radmin VPN address with **HTTP 400 Invalid Hostname**. Meanwhile the status line said "Share this machine's LAN or VPN address". Remote Watch Together therefore could not work without running the app as administrator.

**Second problem found by the same regression.** Leaving a room called `CloseAsync` with no time limit, and the relay never answered a close frame. When Disconnect arrived while a sync message was being handled (the receive loop not parked in `ReceiveAsync`), Disconnect never finished. Connect stayed disabled until restart. The same hang applies to an older relay or a dropped VPN link that never answers.

**Failing regressions first** (`watch-remote-relay-before-20261023.trx`, 3 failures / 1 pass, 20.9 s):
- A peer connecting to `ws://26.113.169.238:<port>/ws/<room>` (Radmin VPN address of this machine) failed with `400` instead of `101`.
- Disconnect during a message handler against the real relay did not complete within 10 s.
- Disconnect against a relay that never answers the close frame did not complete within 10 s.
- Plain HTTP and wrong-path requests were refused with 404 (retention check; passed before and after).

**Repair** (`516c23e`):
- The relay accepts WebSocket upgrades on a dual-mode `TcpListener`, which needs no URL reservation.
- The handshake reads at most 16 KB within 10 s. Non-WebSocket requests and unknown paths get 404; a bad version or key gets 400.
- The relay answers each close frame.
- The client bounds its close handshake at 3 s and then aborts.

**After:** the 4 new checks plus the 11 existing relay/Watch Together service checks passed, **15/15** (`watch-remote-relay-after-20261023.trx`, 5.6 s).

**Firewall note.** Listening on all interfaces makes Windows Defender Firewall ask once per executable path. During the post-repair runs, Windows created "Query User" **Allow** rules (Private, Public; TCP and UDP) for this worktree's `testhost.exe` and, in the two-app UI case, its app executable. Someone answered those prompts; this session clicked nothing. Remove the rules if they are unwanted. The real app will ask the same question the first time a user starts the relay. For Radmin VPN, the network profile is Private.

**Not yet qualified:** a real second machine joining over Radmin VPN or LAN, real media on both ends, and spoken audio. This needs the user (see "Waiting on the user").

## 2. Manga reading progress was never kept

**Problem.** `MangaViewModel` stored no chapter or page. After closing the reader tab or the app, the user had to find the chapter again and scroll back to their page. Every earlier Manga QA record listed "durable reader progress" as unqualified because nothing existed to qualify.

**Failing regression first** (`manga-progress-before-20261023.trx`, 1 failure, 11.7 s). In the real app with an isolated profile and a localhost MangaDex fixture with two chapters, the test opened chapter 2, scrolled to its final page and restarted the app on the same profile. The chapter list offered no way to continue.

**Repair** (`27826ef`):
- Opening a chapter records the exact MangaDex manga ID and chapter ID in `manga_reading_progress.json` in the profile's local data. The native reader records the top visible page once scrolling settles (300 ms).
- The chapter list shows **Continue Ch. N · page P of M**, with accessible name "Continue chapter N from page P". It reopens that chapter and keeps the page aligned while earlier pages decode. Page reports are ignored until the restored page is reached.
- A website-only chapter is offered as "Continue on the website".
- Honest limits: only the exact saved chapter ID is offered, so a renumbered or re-uploaded chapter shows no Continue choice. A damaged progress file is ignored and replaced by the next chapter read. The reader's timers stop when its tab closes.

**After:**
- The restart regression and a non-UI regression pass (`manga-progress-after-20261023.trx`, 2/2, 6.7 s). The non-UI check covers exact-ID matching, restore gating, Back refresh, a renumbered chapter and a damaged file.
- The related Manga, lifecycle and two-app Watch Together UI cohort passed **41/41** in 1 min 52 s (`manga-wt-cohort-20261023.trx`). It includes closed-reader collectibility, two retained reader tabs keeping independent scroll positions, the tall final page, external reader and pagination journeys, and the two-app native Watch Together reconnect case on the repaired relay.

**Not yet qualified:**
- Progress against live MangaDex chapters. The fixture is localhost; the logic is provider-independent, but a live sample is still open.
- Website-chapter page position. Only the chapter is kept for website chapters.

## 3. Live AniList episode check in the real app

The owned, isolated real-app profile (`.artifacts/implementation/notifications-live-20261023/run-225511`) seeded three favorites from a keyless live AniList query made at run time:
- The Apothecary Diaries Season 3 (AniList 195516), one episode behind.
- Black Clover Season 2 (AniList 195604), one episode behind.
- Detective Conan (AniList 235), current.

The app's own startup check (5 s after launch) queried live AniList and:
- advanced exactly the two behind favorites (Sub 3 → 4 and Sub 2 → 3) and left Detective Conan at Sub 1217;
- wrote two alert-history rows with exact AniList/MAL IDs and years at 19:55:17 UTC;
- closed normally (exit 0) 31 s after launch.

No code changed for this check.

The **native toast was not observable**. The primary monitor was showing the user's own full-screen video. Windows suppresses notification toasts over full-screen apps, and the app asks Windows to respect quiet time. The 30 screen captures from that corner showed only the user's video, so they were deleted. The run script no longer captures the screen. The app does not log whether Windows displayed or queued the toast.

**Still open:**
- An observed toast at a time the user is not in full-screen.
- A 30-minute same-process second check. The monitor loop waits 30 minutes between checks, and keeping a real window open that long would block the shared desktop for every other session.

## Resource retention

No new resource evidence beyond the cohort above. Still open:
- The one uncollected closed Manga pair from the October 7 long sample.
- Players retained only while an accessibility client observes them.
- Broader sustained, transfer, cache and reader resource acceptance.

## Waiting on the user

1. **Real-account MAL.** Create a MyAnimeList API client at https://myanimelist.net/apiconfig. Use App Type "other" and redirect URL `http://127.0.0.1:8765/mal/callback/`. Then run:

   ```
   .artifacts/implementation/mal-real-account-20261023/start-mal-sign-in.ps1
   ```

   It waits for the desktop lock, opens the app in its own profile, and releases the lock when the app closes. In Settings → MAL, paste the Client ID, choose Connect MAL, sign in on MyAnimeList yourself and approve. Then sync the library from My List. No password, token or key is typed or stored by a session. Writing progress to the real account (automatic episode progress) needs a separate, explicit yes and a chosen episode.
2. **Remote Watch Together.** Needs a second machine on Radmin VPN or the same LAN running this branch's build.
   - On this machine: Watch Together → Start Relay (port 8000). Allow the Windows Firewall prompt for Private networks.
   - Connect with server `localhost` and a room name.
   - On the other machine: server `26.113.169.238` (Radmin VPN) or `192.168.1.14` (LAN), with the same room.
   - Then play, pause and seek the same episode on both and listen for audio.
3. **Native toast.** Watch the bottom-right corner while running the live notification script, at a time nothing is full-screen.
4. **Firewall rules.** Four "Query User" Allow rules (Private, Public) now exist for this worktree's `testhost.exe` and app executable. They were created by an answer to Windows' prompt that this session did not give. Remove them if they are not wanted.

## Evidence

Results are under `.artifacts/implementation/build/results/` in this worktree:
- `watch-remote-relay-before-20261023.trx`
- `watch-remote-relay-after-20261023.trx`
- `manga-progress-before-20261023.trx`
- `manga-progress-after-20261023.trx`
- `manga-wt-cohort-20261023.trx`

Release build is in the worktree's `.artifacts/implementation/build` (about 0.9 GB). Build: zero warnings and zero errors. No user profile, permanent download or protected QA row was used. No media was downloaded.
