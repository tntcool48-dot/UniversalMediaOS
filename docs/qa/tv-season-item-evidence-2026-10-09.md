# Strict TV season item evidence — October 9, 2026

The actual Breaking Bad season retry still stops at **S1E7, keeping 6/7 completed episodes**. This batch repairs an extraction contract gap; it does not qualify the final episode or close full-season acceptance.

## Observed problem and repair

The normal second-monitor TV Shows → My Library → Breaking Bad → Download season journey reused six existing permanent units, then found no independently verified native source for S1E7. A separate normal source lookup for **7. A No-Rough-Stuff-Type Deal** settled at zero verified and four unverified candidates: one native stream and three explicit website fallbacks. The native candidate showed unknown item and audio language. Successful HLS byte validation did not establish episode identity.

The strict download requirement was applied by the C# coordinator after extraction, but was not passed into the extractor. The extractor could stop at a static native result or captions without independent item evidence. Strict requests now select `resolve-verified`; static and browser/frame extraction continue within the existing bounded operation for item evidence. Ordinary streaming retains its early playable results. Unknown native fallbacks remain unknown, and the final C# identity/unit/audio checks still reject them for strict publication. Caption availability does not certify spoken audio. Existing alternative coordination, browser ownership and cleanup remain in place.

A focused pre-repair backend diagnostic returned only a website fallback, unlike the actual app's earlier unknown native candidate. It therefore did not prove why that particular live candidate lacked metadata. The contract gap is demonstrated by code and controlled regressions; the live provider's missing independent item observation remains unresolved.

## Verification

- Reused Release build location; build passed with zero warnings/errors. **20 focused C# checks** passed with zero failures/skips, including strict and ordinary request forwarding. [TRX](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/strict-item-extraction-20261009.trx).
- **44 relevant Python checks** passed: `test_audiovisual_native_frames`, `test_audiovisual_item_evidence`, and `test_audiovisual_dash`, run from `tools/tests`. New cases cover the strict CLI contract, continuation beyond unknown static/native or captions, truthful exhaustion and final owned-browser close. Existing item contradictions and DASH regressions remain included. An earlier invocation from the repository root failed to import the item-evidence module's sibling helper; the corrected working directory passed without changing the import or assertions. No broader local suite was rerun.
- Rebuilt actual app **PID 67008**, start **2026-10-09T04:25:11.8913738Z**, isolated `catalog-season-20261009/profile`; returned window origin **2880,478**, size **1280×800**, on the second monitor. The normal Download season retry again kept six units and stopped with no independently verified native source for S1E7. Source search logged **07:26:18–07:27:01 Asia/Amman**. No seventh file, native player or website was opened. The owned app then closed normally.
- Source and deployed extractor SHA-256 matched **29F8550B4F81AC41C231DE16C1FDD789680140A7DD55208370E29FC1F8AFA3ED**. Rebuilt WPF DLL SHA-256 **E892E42A74800D5DB5F1517E2CB4210EAD32CCB271DEF1B8F6FF3873DCCF01DE**. Launch evidence records base `182f152` plus the uncommitted strict-item repair; it is not a clean-commit manifest. [Launch](C:/Users/user/animeapp/.artifacts/implementation/catalog-season-20261009/strict-retry-launch.json), [desktop observations](C:/Users/user/animeapp/.artifacts/implementation/catalog-season-20261009/strict-retry-observations.json).

## Data and limits

Six media hardlinks reused **2,028,915,092 bytes** from the retained reference QA profile, with small metadata/caption copies and an isolated database/configuration. No duplicate 2 GB media copy was made. All **19 protected original files** retained their length, SHA-256 and UTC modification time after the actual retries; no original SQLite/configuration was changed. [Final comparison](C:/Users/user/animeapp/.artifacts/implementation/catalog-season-20261009/protected-originals-after-strict-retry.json).

The first comparison incorrectly treated the JSON timestamp converted to `System.DateTime` as a literal string, reporting 19 differences despite identical hashes and lengths. Comparing normalized UTC times corrected the verifier; no original timestamps or data were changed. Its failed comparison remains retained beside the corrected evidence.

No grouped acceptance gate closes here. Full TV season **6/7**, catalog-qualified DASH, broader matching/cuts/audio/full-duration captions and packaged acceptance remain open. Next: capture bounded missing-item evidence from the actual provider owner, and repeat the season journey only after new evidence or a repair. One hosted full source checkpoint will follow this coherent source batch; the latest completed full pass remains `9fe263b`, which predates these extraction edits.
