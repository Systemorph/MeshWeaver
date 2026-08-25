---
Name: Error logs now report real faults, not routine teardown
Category: Fix
Description: Cancelled creates and deletes, client disconnects, and truncated log captures were all filed as errors — burying the failures that actually needed attention.
Icon: Bug
Order: -20260825
---

# Error logs now report real faults, not routine teardown

Four different situations were being reported as errors when nothing had gone wrong, and between
them they produced most of the entries in the platform's error log — and most of the tickets its
automatic triage opened. The genuine failures were still there, indistinguishable from the noise.

- **A cancelled create or delete is no longer an error.** When a create or delete is cut short —
  the caller navigated away, a workspace cleanup cascaded, the service was shutting down — nothing
  failed and nothing was written. It is now recorded as what it is, and the answer you get back
  says "cancelled" and that retrying is meaningful, instead of "unexpected error".
- **A client that disconnects mid-stream no longer looks like a broken request.** A browser or
  device closing a live connection at the wrong instant surfaced as a failed service call, with a
  stack trace pointing into the platform.
- **Error reports carry their diagnosis again.** The log reader rebuilt each error from the lines a
  server writes, but merged the output of every replica first — so another replica writing at the
  same moment could cut an error in half and throw away its exception and stack trace. Errors are
  now rebuilt per server, so what you read is the whole thing.
- **An error with no content is never filed as one.** A capture that arrived with no message, no
  exception and no stack could only be identified by which component logged it, so every later
  content-free capture from that component piled onto the same ticket. Those are now reported as
  what they are — a capture problem, naming the component — and the ticket you get for a real
  failure stays about that failure.

A cancellation that happens *after* a delete has already removed something is still reported loudly:
that one leaves work half-done, and it is exactly the case the old wording was borrowing its urgency
from. A timeout is likewise still a failure, even though the platform reports timeouts using the
same mechanism as cancellations.
