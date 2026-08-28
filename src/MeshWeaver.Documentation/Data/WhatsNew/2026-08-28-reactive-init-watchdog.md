---
Name: Data initialization watchdog is fully reactive
Category: Fix
Description: The data-initialization time-box now waits reactively on the real completion signal, so startup failures surface fast and precisely instead of racing a background timer.
Icon: Sparkle
Order: -20260828
---

# Data initialization watchdog is fully reactive

A hub whose data sources hang or fail during startup is now settled by a subscription to the
actual completion signal rather than a background timer race. The behaviour a user sees is
unchanged in the good cases and sharper in the bad ones: a hung initialization still trips the
same time-box and answers requests immediately with a clear "did not complete within…"
diagnostic, a failed initialization reports the specific error, and a page closed mid-startup no
longer risks a stray timeout being logged minutes later.
