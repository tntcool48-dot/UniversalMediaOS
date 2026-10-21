# Settings save, restart and protected keys in the real app — October 21, 2026

Part of the open "Settings accessibility/action states" item and the [skipped-test inventory](skipped-test-inventory-2026-10-05.md). Branch **codex/app-recovery** after **571a60e**. Each case launches its own AppFixture profile on the second monitor, so the shared test profile never changes.

## Defect found while writing the cases

- Settings Save writes every field.
- A key saved by another Windows account or computer reads as empty here (the [portable settings repair](portable-protected-settings-2026-10-21.md)), so its field shows empty.
- Saving Settings for any other reason therefore replaced that stored key with an empty value, and the original was lost. It would still have read on its own account if the profile moved back.

**Repair.** `DomainHotSwapper` no longer rewrites a protected setting whose new value equals what callers already read (`SetSetting`, `SetSettings`, `SetSettingAsync`). An unreadable key stays until a new key is entered. Legacy plaintext still migrates to the protected form on load.

`ProtectedSettings_SavingUntouchedFieldsKeepsAnotherAccountsCredential` failed before the repair (`protected-unchanged-save-before-20261021.trx`). After it, the protected-setting cases pass **18 / 18** (16 in `CoreHardeningRegressionTests`, 2 in `ConfigurationHardeningTests`; `protected-unchanged-save-after-20261021.trx`).

## Three placeholders are now real UI tests

| Test | Journey |
| --- | --- |
| `T1_Settings_02_Set_Domain_Hot_Swap_URL` | Providers & Scrapers → "Scraper Seed URL" = `https://seed.example.test/` → the visible Save Settings → the "Settings Saved" message box is dismissed with OK. `config.json` holds the URL. After a normal close and relaunch, the field shows it. |
| `T1_Settings_05_Encrypt_and_Save_API_Keys` | The "Google Books API key" password box is filled through UI Automation and saved. The stored value starts with `dpapi:v1:`, the plaintext appears nowhere in `config.json`, and a fresh reader on the same Windows account reads the key back. |
| `T2_Settings_05_DPAPI_Cryptography_Failure_Fallback` | The profile starts with two keys this account cannot decrypt: Google Books in the current marked format and TMDB in the legacy unmarked one. Extra entropy stands in for another account. The app logs the one-time "saved by another Windows account or computer" warning for each. Saving without touching them keeps both byte-identical. Entering a new Google Books key replaces only that one; it is stored protected, reads back, and the TMDB value stays. |

## Results

| Run | Result | TRX |
| --- | --- | --- |
| Foreign-key case against the app rebuilt from the committed `DomainHotSwapper` | **Fail**: the stored Google Books value became `""` | `settings-foreign-key-ui-before-20261021.trx` |
| The three new cases, repaired build | **3 / 3**, 39 s | `settings-placeholders-real-20261021.trx` |
| Whole `SettingsTests` class | **6 passed, 5 skipped**, 44 s | `settings-placeholders-after-20261021.trx` |

The before-run used a scratchpad copy of the repaired file, restored afterwards with a matching hash. Skipped methods in the suite drop from **22 to 19**: nine Downloads, five Search and five Settings.

## Limits

- No real second account or computer was used: protection with extra entropy fails decryption the same way.
- Encryption failure (`ProtectedData.Protect` throwing) is not injected. By code inspection, it logs an error and throws before any setting changes, so nothing is written. Its visible Settings outcome is not qualified.
- An unreadable key cannot be cleared from Settings, because its empty field equals what is read. Entering a new key replaces it. A separate "clear" action would be a product decision.
- "Hot swap" here means the value persists across Save and restart. The Save message asks users to reopen open Movie, TV, Cartoon or Book tabs, so live switching of an open tab is not claimed.
