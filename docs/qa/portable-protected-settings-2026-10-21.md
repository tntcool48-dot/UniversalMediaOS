# Copied configurations keep another account's credentials unreadable — October 21, 2026

Part of the open "portable configuration migration" item. Branch **codex/app-recovery** on **0c8f634**; temporary configuration files only.

## What "portable" covers here

- `config.json` has always lived in `%APPDATA%\UniversalMediaOS`. Only the database once sat beside the executable, and that migration, including denied-then-retried publication and a real profile snapshot, passed on October 6 ([copied-profile QA](copied-profile-compatibility-2026-10-06.md)).
- A moved profile folder or drive alias is handled by the journal's location binding (October 20).
- What remained was a configuration **copied to another Windows account or computer**. Six credentials (`QBitPassword`, `MalOAuthToken`, `MalOAuthRefreshToken`, `MalClientSecret`, `TmdbApiKey`, `GoogleBooksApiKey`) are protected with DPAPI for the current user, so they cannot be decrypted after such a copy.

## Defect

New-format values (`dpapi:v1:…`) were already read as empty there, with an error logged on every read. Values written **before that marker** were not:

- The load-time migration treated an undecryptable legacy blob as *plaintext* and protected its ciphertext as the new credential.
- From then on the app returned the other account's base64 blob as the token or password. That would send garbage to MAL, qBittorrent or TMDB.
- It also destroyed the original value, which would still have read back on the original account.

Reproduced before the repair with a blob this account cannot decrypt: the setting read as `AQAAANCMnd8B…`, which is the DPAPI header in base64 (`portable-protected-settings-before-20261021.trx`).

## Repair

A legacy value whose bytes start with the fixed DPAPI blob header (version 1 and provider GUID `df9d8cd0-1501-11d1-8c7a-00c04fc297eb`), but which this account cannot decrypt, is now:

- left exactly as stored, so moving the profile back still reads it;
- read as an empty setting, so the feature behaves as not configured;
- reported once per key: "Protected setting '…' was saved by another Windows account or computer and cannot be read here; enter it again".

The marked format uses the same one-time warning instead of an error on every read. Genuine legacy plaintext still migrates to the protected form. Re-entering the credential saves and reads normally.

## Verification

`ProtectedSettings_CopiedFromAnotherAccountStayUnreadableInsteadOfBecomingCredentials` checks:

- a legacy foreign blob and a marked foreign value both read empty and stay byte-identical in the file;
- a legacy plaintext key migrates to `dpapi:v1:`;
- a re-entered token reads back after reload.

The new case and every class that sets or reads protected keys pass **119 / 119** in 1 min 13 s (`portable-protected-settings-related-20261021.trx`). Those classes cover configuration and core hardening, copied profiles, MAL sync and automatic progress, qBittorrent ownership, catalog routing and paging, and download pinning.

## Limits

- No real second account or computer was used: protection with extra entropy stands in for one, since decryption fails the same way.
- **Not changed:** an explicitly configured absolute path (`DatabasePath`, `DownloadDirectory`) that points to a drive missing on the new computer is still honoured as given, so persistence fails rather than silently moving to the default. Whether to fall back is a product decision.
