# FlaUI fixture closes the app through its own window — October 21, 2026

Follow-up to the [close-path note](close-path-journal-logs-and-player-database-lifetime-2026-10-20.md), test harness only. FlaUI's `Application.Close()` posts WM_CLOSE to `Process.MainWindowHandle`. On October 20 that resolved to a visible, unowned tooltip popup instead of the WPF window, so the close never reached the app. With FlaUI's default kill fallback, `AppFixture.Restart()` would then silently replace a normal close, and its progress drain, with a kill. `GetMainWindow` uses the same lookup and could return the popup as "the main window".

**Change** in `AppFixture`:

- `CloseMainWindow(timeout)` asks the window titled "Universal Media OS" to close through its own UI Automation window pattern, as the title-bar button does, and waits for the process to exit.
- `Restart()` uses it and **throws** if a normal close does not happen within 20 s. It then finds the relaunched window by title.
- `Dispose()` tries it first, then falls back to the old process close and kill, which covers a modal dialog or hung window.
- The constructor's main-window fallback matches the title instead of calling `GetMainWindow`.

**Run:** the five real-app tests that restart the app (episode notifications, MAL sync, My List identity, voice cast, resume durability) and six navigation cases that cover the new disposal path: **11 / 11**, 2 min 33 s, Debug build, second monitor (`appfixture-window-close-20261021.trx`). Hosted runners have no cursor resting on a control, so the hazard was local; the hosted checkpoint still covers every other fixture user.
