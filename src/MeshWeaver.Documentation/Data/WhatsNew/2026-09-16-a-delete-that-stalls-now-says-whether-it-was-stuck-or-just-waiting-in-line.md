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

The platform now takes a reading of its own work queues at the moment it gives up, and puts it on
the report. If nothing was queued, that is a definite answer: nothing was waiting for a turn, so the
hold-up was in storage. If something was queued, the report names it, with how deep the queue was
and how long anything has had to wait there — a lead to follow rather than a verdict, and the
wording says so.

The same reading also answered a question that had been open for a month about how the portal
schedules its database work. Taken on a live portal over fourteen hours, the write queue everyone
suspected turned out never to have made anything wait more than a fifth of a second, while the read
queue beside it — carrying four orders of magnitude more traffic — is the one that actually queues.
Nothing was tuned on the strength of that: it replaces a suspicion with a measurement, which is what
a decision to change either number would have needed in the first place.
