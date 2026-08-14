---
Name: Failures come back straight away
Category: Fix
Description: When a page asked for something the portal could not serve, the answer could take a full minute to arrive — or never arrive at all.
Icon: Sparkle
Order: -20260814
---

# Failures come back straight away

When part of a page asked the portal for something it could not serve — a file from a component
that was restarting, say — the "no" was sent back by a slower internal route than the original
question travelled on. Usually that was fine. Occasionally the answer was simply lost, and whatever
was waiting for it waited a full minute, or forever.

Failure answers now come back the same way the request went out. Something that cannot be served
says so immediately, so a slow corner of a page stays a slow corner instead of holding the rest up.
