---
Name: A merged fix is ready to deploy within the hour
Category: Fix
Description: A fix that passed its tests could wait up to three hours before a deployable release existed; that wait is now about an hour.
Icon: Sparkle
Order: -20260817
---

# A merged fix is ready to deploy within the hour

A fix that was merged and passed its tests could still sit for up to three hours before a deployable
release existed — so there was nothing for an installation to update to, however healthy everything
looked. The delay came from how often delivery checked whether the newest code had been released,
which was every three hours.

That check now runs hourly, and merges are grouped into at most one release an hour rather than one
every three. A fix becomes deployable about half an hour after it merges, and at worst a little over
an hour.

When your installation then *applies* an available release is a separate setting, controlled by its
update policy — this change is about how quickly there is something to apply. An administrator who
wants a specific fix immediately can still trigger the update by hand rather than waiting.

Nothing about what gets released changed: a release still ships only when the full set of images
builds, and only for code whose tests passed.
