---
Name: Re-installing an unchanged package no longer rewrites its content
Category: Fix
Description: Package installs now recognize unchanged content in both directions, so version histories stay clean and installs stay fast.
Icon: Sparkle
Order: -20260811
---

# Re-installing an unchanged package no longer rewrites its content

Installing or updating a package compares each piece of content against what is already there
and only writes what really changed. That comparison could misread an unchanged item as
changed when one side had been stored in a raw form — every install then rewrote the item,
polluting its version history and slowing installs down.

The comparison now normalizes both sides the same way before comparing, so an unchanged
item is recognized as unchanged no matter which form it was stored in. Real changes are
still always detected and written.
