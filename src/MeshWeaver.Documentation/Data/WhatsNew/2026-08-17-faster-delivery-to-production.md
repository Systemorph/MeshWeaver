---
Name: Fixes reach your portal within the hour
Category: Fix
Description: A merged fix could previously wait up to three hours before an image existed to update to; the wait is now about an hour.
Icon: Sparkle
Order: -20260817
---

# Fixes reach your portal within the hour

A fix that was merged and passed its tests could still sit for up to three hours before a deployable
image existed — so a portal had nothing to update to, however healthy everything looked. The delay
came from how often delivery checked whether the latest code had been published, which was every
three hours.

That check now runs every hour, and merges are grouped into at most one release an hour rather than
one every three. In practice a fix is available to your portal about half an hour after it merges,
and at worst a little over an hour, instead of up to three.

Nothing about what gets published changed: a release still ships only when the full set of images
builds, and only for code whose tests passed.
