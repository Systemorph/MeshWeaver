---
Name: A brand-new portal comes up instead of waiting for an image it never had
Category: Fix
Description: A freshly provisioned portal could refuse to come up at all, answering 503 at its address while its pod ran perfectly well. The safety check that stops a bad update from replacing a good one was counting a first-ever build failure as something the new version had broken — on a first start there is no older version to fall back to, so it now reports the failure and lets the portal serve.
Icon: Rocket
Order: -20260916
---

# A brand-new portal comes up instead of waiting for an image it never had

A portal created for the first time could stay unreachable for hours — its address answered
*503 Service Temporarily Unavailable* while, behind it, the portal itself was running and healthy.

The check behind it is a good one. Before a running portal accepts an update, it rebuilds the page
types stored in it; if a type that used to build stops building, the update is refused and **the
previous version keeps serving**. That is what stops a bad update from taking a working portal down.

On the very first start there is no previous version. Nothing has ever been built, so every type is
new — and a type that fails its first build was being read as one the new version had *broken*. The
portal refused to take itself into service to protect an older self that never existed, and nothing
else was there to answer, so visitors got the 503.

**A first start is now recognised for what it is.** When nothing on the portal has ever been built,
a build failure is reported rather than treated as damage, and the portal serves. The moment
anything has been built there, the check goes back to its full strictness — an established portal
still refuses an update that breaks a page type that used to work.

Failures on a first start are no longer silent either. They are named in the portal's own health
report, so a page that cannot be built yet — usually one whose feature is still being installed —
shows up as something to look at rather than as an address that does not answer.

Nothing to do; this applies to every newly created portal.
