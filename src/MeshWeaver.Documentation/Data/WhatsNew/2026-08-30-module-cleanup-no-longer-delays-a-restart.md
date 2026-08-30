---
Name: Module cleanup no longer delays a restart
Category: Fix
Description: Cleaning up leftover module files now happens after the portal is already serving, so a restart can never hang on slow storage doing housekeeping.
Icon: Sparkle
Order: -20260830
---

# Module cleanup no longer delays a restart

When the portal starts, it tidies up module files that older versions left behind. On deployments
whose data lives on network storage, that tidying can take minutes — and it used to run *before*
the portal started answering requests. A restart could therefore look stuck for so long that the
platform gave up, killed the starting portal, and tried again, in a loop: an update that never
completed, caused entirely by cleanup work nobody was waiting for.

The cleanup now runs right after the portal is up and serving. Restarts and updates come up as fast
as the portal itself, regardless of how much there is to tidy — and the tidying still happens, with
exactly the same care as before: nothing in use is ever touched, and anything a busy moment skips
is picked up on a later pass.
