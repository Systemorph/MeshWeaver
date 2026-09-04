---
Name: A code cell that cannot prove it is current now says so
Category: Fix
Description: When a run was recorded but what it ran was not, a code cell no longer shows its output as up to date — it says the output is unverified, instead of quietly vouching for a result it cannot account for.
Icon: Sparkle
Order: -20260904
---

# A code cell that cannot prove it is current now says so

Every time you press **Run** on a code cell, the platform records the run on the cell — when it
happened, who started it, and a fingerprint of the exact code that was submitted. That fingerprint
is what lets the cell tell you *"code changed — re-run"*: it compares the fingerprint of the run
against the code you are looking at now.

Recording that is a write like any other, and a write can fail. When it did, the cell was left
saying it had run while saying nothing about **what** it had run — and the indicator read that as
"nothing to worry about". The output pane showed a result, the toolbar showed a plain Run button,
and there was no way for you to tell that the two might not belong together. A missing warning is
inconvenient; a cell vouching for output it cannot account for is worse, because you believe it.

A cell now has three honest things to say instead of two. If it never ran, it stays quiet — there
is no output to be wrong about. If it ran and recorded what it ran, it tells you whether the code
has moved since, exactly as before. And if it ran but the record is incomplete, it now says the
output is **unverified** and invites you to re-run, rather than presenting it as current.

Older cells — those last executed before the fingerprint existed — fall into that third case too.
They are marked unverified rather than stale on purpose: telling you a cell is definitely out of
date when nobody knows would make the warning worth ignoring, and the warning is only useful while
you still believe it.
