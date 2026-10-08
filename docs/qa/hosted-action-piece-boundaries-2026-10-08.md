# Hosted file-action and rapid-pause boundaries — October 8, 2026

The [full 23e542a run](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37785953251) failed with **1,046 passes / 22 skips / two failures / 1,070 total**, **11 minutes 19 seconds** of test execution. All three new file-access cases passed.

The rapid-pause session test had already received a new verified piece and stopped correctly, but rejected **0.05379236148466917%** through an unrelated 0.1% minimum. Its assertion now requires actual progress beyond the previous stopped piece state and below completion; weighted queue progress, persisted identity, cache/partial retention and registered-manager/DHT cleanup assertions are unchanged.

The localization failure was the final English Play action, after the header/Open folder had appeared while the asynchronous folder refresh was still populating file actions. Both language legs now wait up to three seconds for the expected file Play label within the Downloads view before their unchanged text/help/file assertions. No production behavior changed. The three focused real native-pause/localization cases passed **three checks / zero failures/skips in 20 seconds**, with zero build warnings/errors. Production behavior is unchanged. Failed full results remain failed, and exact hosted validation of the final source is pending.
