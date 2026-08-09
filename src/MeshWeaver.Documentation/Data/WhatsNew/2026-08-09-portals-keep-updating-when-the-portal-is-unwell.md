---
Name: A portal no longer stops updating itself when something else goes wrong
Category: Fix
Description: A portal that could not write its own "latest version available" note used to stop applying updates entirely, silently, for as long as the pod ran.
Icon: Sparkle
Order: -20260809
---

# A portal no longer stops updating itself when something else goes wrong

A portal checks for a new version a few times a day and rolls itself forward. Before applying an
update it also wrote a small note to its own settings — the newest version it had seen, and when it
last looked — which is what the Update Policy tab shows you.

If that note could not be written, the update was abandoned along with it. One production portal
spent 37 hours on an old version for exactly this reason: the version check itself was working
perfectly and had picked the right new version every time, but a piece of bookkeeping stood in front
of it. Nothing about the portal looked broken, so the drift was only noticed by comparing version
numbers by hand.

Applying the update no longer depends on that note. If the note cannot be written the portal says so
and rolls forward anyway — which matters most precisely when the portal is unwell, because the newer
version is usually what puts it right. The log now also names the version it is about to apply, so a
portal that fails to update says which update it was trying to install.
