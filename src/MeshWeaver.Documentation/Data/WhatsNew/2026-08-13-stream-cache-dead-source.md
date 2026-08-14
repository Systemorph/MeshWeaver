---
Name: Pages no longer stall on a stream whose source went away
Category: Fix
Description: A cached data stream whose source was torn down is replaced instead of served, so a read gets an answer instead of a stale value and silence.
Icon: Sparkle
Order: -20260813
---

# Pages no longer stall on a stream whose source went away

When the data source behind a view was torn down and rebuilt, the portal could keep handing out a cached view of the source that had just disappeared. A page bound to it showed the last value it had seen and then went quiet forever — no update, no error, nothing to retry — and a save routed through the same path could report success while writing to a stream nobody was reading.

Streams now know what they were derived from, so a view whose source is gone is rebuilt instead of reused, and a read against a source that is genuinely gone ends promptly rather than waiting out its full budget.
