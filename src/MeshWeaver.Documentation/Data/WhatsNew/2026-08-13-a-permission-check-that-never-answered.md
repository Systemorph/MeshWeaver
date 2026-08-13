---
Name: A stuck permission check no longer freezes creating things
Category: Fix
Description: When the security layer cannot read a node's permissions, creating, moving or deleting there now fails immediately and says the check could not be established — instead of sitting silently until the request gives up.
Icon: ShieldError
Order: -20260813
---

# A stuck permission check no longer freezes creating things

Creating a node runs a permission check first, and that check reads the grants and policies of the
target's location and every location above it. Normally those reads are instant. When one of them
cannot be answered — the copy of the data being asked for lives on a machine that is busy or has
just gone away — the check produced **nothing at all**: no yes, no no, not even an error. And a
create waiting on nothing waits forever.

What you saw was a create, move or delete that simply never came back, and eventually an unhelpful
"no response received" from the request itself — naming your own request as the thing that timed
out rather than the read that stalled. Nothing in the failure pointed at the real cause, so the
obvious next step was to try again, into exactly the same stall.

Now the check has a definite ending. If it cannot be established within its budget, the operation
stops and says so: **the permission check could not be established** — naming the location whose
read did not answer. That is deliberately not the same as "access denied". Reporting a stalled read
as a refusal is worse than useless: it tells you to go and request permissions you already have, and
it hides an infrastructure problem behind what looks like a policy decision. The operation still
does not go ahead — it simply stops pretending to know why, and retrying is now meaningful.

A related gap closed with it: asking a node for your permissions could leave the question
unanswered if the underlying read finished without producing a value. It now always answers.
