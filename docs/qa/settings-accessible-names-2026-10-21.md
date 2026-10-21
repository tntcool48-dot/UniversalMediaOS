# Settings controls all have accessible names — October 21, 2026

Part of the open "Settings accessibility/action states" item. Branch **codex/app-recovery** on **31fbb69**; AppFixture sandbox only.

**Audit.** The new `Settings_EverySectionNamesItsInteractiveControls` opens Settings and visits all eight sections: Appearance, Services, qBittorrent, MAL, Providers & Scrapers, Notifications, Privacy, Language. In each, it lists every visible, keyboard-focusable button, field, combo box, check box, slider, radio button and link whose UI Automation name is empty. A screen reader announces such a control only by its type ("edit", "slider"). Non-focusable track parts inside WPF sliders and scroll bars are excluded, because they are never announced.

**Before the repair**, 13 controls had no name (`settings-accessibility-before-20261021.trx`); each had only a tooltip:

| Section | Unnamed controls |
| --- | --- |
| Appearance | dark-mode check box, corner-radius slider |
| Services | download folder field |
| qBittorrent | host, port and username fields, password box |
| MAL | Client ID, Client Secret, Redirect URI, manual token fields |
| Providers & Scrapers | each dub provider's timeout slider, and the new provider's timeout slider |

**Repair.** Each control now has an `AutomationProperties.Name` equal to its visible label, so screen readers say what sighted users read:

- labels that are already localized (Dark Mode, Corner Radius, Download directory, Host, Port, Username, Password) use the same localization markup, so Arabic names follow the Arabic labels;
- the MAL labels are plain English on screen and stay so;
- a provider's slider is named "<provider> timeout", and the new-provider slider "Dub provider timeout", next to the existing "Dub provider name/URL".

The Settings root also gains the automation ID `SettingsView`, so the audit stays inside Settings.

**Verification.** The audit, the other Settings cases, navigation and tab-title localization pass **15 / 15**, with the 8 existing Settings placeholders still skipped (`settings-accessibility-after-20261021.trx`, 10 s).

**Limits.** Names only. Focus order, actual Narrator or NVDA output, Arabic screen-reader speech and the remaining action-state checks (for example Repair while preparing) are not covered.
