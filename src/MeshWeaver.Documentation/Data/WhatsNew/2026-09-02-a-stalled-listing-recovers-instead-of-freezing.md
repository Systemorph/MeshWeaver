---
Name: A stalled listing recovers instead of freezing
Category: Fix
Description: When a data feed's connection to its source timed out once, that listing stopped updating for as long as the server kept running. It now rebuilds itself and says so while it is stale.
Icon: ArrowSync
Order: -20260902
---

# A stalled listing recovers instead of freezing

Some listings in the portal are kept live by a feed: a component subscribes to whatever supplies the
data, and pushes each change into the page. The store's package listing is one of these.

Setting that feed up means asking another component for a subscription, and asking can fail — the
other side is busy, restarting, or a reply gets lost in transit. That is ordinary and momentary.

**What was not momentary was the consequence.** A single failed request ended the feed. The listing
kept displaying whatever it had at that moment and never updated again, for as long as that server
process kept running. Nothing retried it, and from the outside it looked completely normal: the page
rendered, the data was there, it was simply frozen in the past. The only cure was restarting the
component.

The feed now **rebuilds itself** after a failure, backing off a little further each time up to once a
minute, for as long as it takes. It reports each failure and says the listing is stale until the
rebuild lands — where before it reported, accurately and once, that the listing would receive no
further updates at all.

There was a subtlety that made this more than adding a retry, and it is worth recording because it
would have been an easy thing to get wrong: the feed **remembers** its connection so that several
parts of the page can share one. That memory also remembers the failure. So simply asking again
would have handed back the same failure instantly, for ever — a retry that looked completely correct
and could never have worked. The remembered failure is now discarded before the rebuild, which is
what makes asking again mean anything.

This does not address why the original request timed out; that is tracked separately. What changes is
that one lost reply no longer costs a listing until the next restart.
