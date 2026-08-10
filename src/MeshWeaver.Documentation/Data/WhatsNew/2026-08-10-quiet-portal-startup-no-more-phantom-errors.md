---
Name: Portal startup no longer logs phantom errors
Category: Fix
Description: Every portal start logged internal errors from a race it always won anyway; startup is now ordered so the race cannot happen.
Icon: PlugConnected
Order: -20260810
---

# Portal startup no longer logs phantom errors

Every time a portal started, two alarming-looking internal errors appeared in its logs —
a low-level failure from the messaging engine, reported at error severity, on every
single boot. The portal always recovered on its own within seconds, but the errors were
real enough to trip automated monitoring and file incidents for starts that were, in
fact, perfectly healthy.

The cause was ordering: two core services came up eagerly and tried to attach to the
cross-process messaging layer before that layer had finished initialising. A retry loop
then quietly won the race a moment later — working, but by collision rather than by
design.

Startup is now properly ordered. The attachment waits for the messaging layer to
announce it is ready and then connects exactly once — no race, no retry, and no phantom
errors in the log. If the messaging layer genuinely never comes up, that is now reported
loudly as the real problem it is, instead of being buried under routine noise.
