---
Name: A leaked callback inside a hosted hub is now reported
Category: Fix
Description: A request that was never answered inside a hosted hub used to cost every shutdown a silent two-second wait and was invisible to the leak report; the report now names the hub and the request, and hosted hubs drain under the same budget as their owner.
Icon: Sparkle
Order: -20260903
---

# A leaked callback inside a hosted hub is now reported

When the platform shuts a hub down it first waits, briefly, for any request that hub is still
expecting an answer to. If an answer never comes, that wait runs to its budget and the leak is
supposed to be reported so someone can fix the request that was abandoned.

For hubs hosted inside another hub, the report never fired. By the time the owning hub asked its
children whether anything had leaked, the children had already finished and left — taking the
answer with them. Measured on one test suite: 52 of 320 classes were paying that silent two-second
wait, and the report said nothing.

A child now hands its verdict to its owner as it leaves, so the report names the hub and the
request. And a hosted hub is created with its owner's waiting budget rather than a fixed default,
so a whole tree of hubs drains under one policy.
