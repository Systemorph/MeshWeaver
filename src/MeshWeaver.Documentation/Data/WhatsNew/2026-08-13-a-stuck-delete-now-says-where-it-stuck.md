---
Name: A stuck delete now says where it got stuck
Category: Fix
Description: A delete that runs out of time reports the exact node and step that stalled, instead of a bare "timed out" with no detail.
Icon: Timer
Order: -20260813
---

# A stuck delete now says where it got stuck

Deleting a large space walks the whole subtree: it checks your permissions, asks
every node underneath whether it may be removed, and then removes them from the
bottom up. When one of those steps stalled, the delete gave up and told you only
that it had timed out — never which node, never which step, and never how much
had already been deleted. Two people looking at the same message could reasonably
reach opposite conclusions about whether anything had been touched.

The reason was subtle: each level of the operation had its own time limit, and
they were all set to the same 30 seconds. The level closest to the problem — the
one that actually knows which node went quiet — could therefore never be the one
to speak up, because the level above it always ran out first and answered on its
behalf with far less detail.

Now every inner step gets a shorter deadline than the step containing it, so the
level nearest the problem is the one that reports. A stuck delete tells you the
node that stalled, the step it stalled in, and which paths were already removed —
and when the hold-up is that a permission check could not be completed, it says
that plainly rather than implying your content was refused.
