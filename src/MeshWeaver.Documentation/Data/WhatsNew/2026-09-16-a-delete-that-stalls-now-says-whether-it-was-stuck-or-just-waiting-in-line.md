---
Name: A delete that stalls now says whether it was stuck or just waiting in line
Category: Fix
Description: When deleting a large area timed out, the report named the paths it had not finished and nothing else — leaving no way to tell a storage problem from simple congestion. It now states whether anything was queued for a slot at that moment, which separates the two.
Icon: Sparkle
Order: -20260916
---

# A delete that stalls now says whether it was stuck or just waiting in line

Deleting a space, a course or any area with many items underneath is not one operation — it is one
per item, run from the bottom up. When that run stopped making headway, the platform gave up after
thirty seconds and reported which items were still outstanding.

That report was true and still could not be acted on, because "made no headway" has two quite
different causes. Either the storage really had stopped answering — something to go and look at —
or the delete's next step was simply queued behind unrelated work elsewhere in the portal and had
not been given a turn yet. Those call for opposite responses, and the message named neither, so
every occurrence ended in the same guess.

The platform now watches its own work queues across the whole attempt — noting where they stand when
the delete begins, and again when it gives up — and puts that on the report. If nothing was queued
at the end and nothing had to wait for a turn along the way, that rules out congestion as the cause:
the delete was held up somewhere else, and the report says which possibility it has eliminated
rather than claiming to know the answer. If something was queued, it is named, with how deep the
queue was and how long work has had to wait there — a lead to follow rather than a verdict.

Noting where the queues stand at the *start* is what makes the clean answer worth anything. A single
look at the moment of failure would miss the case that matters most: work that waited nearly the
whole time for its turn, got it, and only then stalled — by which point the queue is empty and a
one-off glance would have called it clear.

The same reading also answered a question that had been open for a month about how the portal
schedules its database work. Taken on a live portal over fourteen hours, the write queue everyone
suspected turned out never to have made anything wait more than a fifth of a second, while the read
queue beside it — carrying four orders of magnitude more traffic — is the one that actually queues.
Nothing was tuned on the strength of that: it replaces a suspicion with a measurement, which is what
a decision to change either number would have needed in the first place.
