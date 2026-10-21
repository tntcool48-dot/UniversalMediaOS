# Hosted checkpoint c2138b6 and the Python suite's first node start — October 21, 2026

[Run 39624470657](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/39624470657) for **c2138b6**:

- **.NET suite:** **1,410 passed / 0 failed / 17 skipped of 1,427** in 15 min 26 s. Both earlier runner-timing repairs held: the pressured admission case and the journaled session-end case passed.
- **Python suite:** 229 tests, **1 error**, so the run concluded "failure".

## The Python error

`test_current_provider_api_token_binds_the_original_item_without_refetch` raised `subprocess.TimeoutExpired` while running `node -e …` under a 10-second timeout.

Unittest runs modules and methods alphabetically. This method is the first in the suite to start `node`, so it absorbed the runner's first `node.exe` start. On a fresh hosted runner that start can be slow (for example, while the executable is scanned on first use). The same test passed on the two previous checkpoints. It does not depend on timing: its program is synchronous and prints one JSON value.

## Repair (test only)

Both `node` subprocess calls in `tools/tests/test_audiovisual_item_evidence.py` now use a shared 60-second ceiling, `NODE_TIMEOUT`. The calls had used 10 s and 5 s. The timeout still stops a real hang; it no longer measures the runner's process start.

The module passes **24 / 24** locally in 0.7 s. A cold first start cannot be reproduced on this already-warm machine, so the next checkpoint is the confirmation.
