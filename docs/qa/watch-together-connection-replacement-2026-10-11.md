# Watch Together reconnect ownership — October 11, 2026

A delayed failed connection attempt could disconnect a newer, healthy room. `ConnectAsync` resumed against shared `_socket`/`_cts` fields, and both its failure and caller-cancellation handlers invoked shared disconnect cleanup. Disconnect followed by reconnect could therefore succeed, then unexpectedly become Failed or Disconnected when the earlier UI continuation ran.

Two production-client regressions reproduce the race using real localhost HTTP/WebSocket connections, an isolated configuration, a held UI synchronization context and the production relay. Each establishes a replacement host and independent peer before releasing the first failed/canceled continuation. They require the replacement room, host role, 2.5-second offset and subsequent actual seek-message delivery to remain intact.

- Valid before-repair result: **zero passes / two failures / zero skips, 109 ms**. The delayed failure set the replacement to Failed; caller cancellation changed it to Disconnected. [Before TRX](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/watch-connect-replacement-before-valid-20261011.trx).
- Final affected/related result: **five passes / zero failures/skips, 149 ms**. Both regressions and the existing relay initial-state, outbound-order and size-limit checks passed. [After TRX](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/watch-connect-replacement-after-20261011.trx).
- Reused Release build: **zero warnings/errors**. No GUI app, original profile, media transfer, broad local C#/Python suite or previously passing playback cohort was launched.

The client now captures its socket and cancellation token before connecting. A superseded attempt cannot publish Connected, start a receiver for the replacement socket or run cleanup against it. Caller cancellation still propagates to the original caller. A current connection failure retains the existing failure behavior. This repair does not change room URLs, role/offset protocol, native pause behavior, tab ownership, matching or persistence.

The initial test build lacked an explicit `System.IO` import and did not run. An initial IPv4-literal listener variant timed out before replacement qualification; its result is retained separately and does not establish the product fault. The final localhost setup follows the existing relay cases. Those setup corrections preceded the valid before-repair reproduction. No assertion or deadline was relaxed.

The preceding Unicode source **36aacccccf8af0a6c6d54d449aa00e82615575ea** completed its single [hosted checkpoint](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/38031843551): **1,165 passes / 22 existing skips / zero failures / 1,187 total**, **11 minutes 10.108 seconds**, with successful build and Python syntax. Its original TRX was downloaded once; this result excludes the reconnect repair. The existing published package remains separately tied to 6ff71bb and excludes both changes.

Remote-network/provider/two-window acceptance and the remaining shared lifecycle gates stay open. The user's Eris reply did not confirm spoken English, so that audio gate remains unverified. Books remain deferred; Arabic cartoons remain last. This batch adds one checked implementation step, closing no grouped gate: **83 checked steps / 17 grouped open gates / 15 non-Books gates**.
