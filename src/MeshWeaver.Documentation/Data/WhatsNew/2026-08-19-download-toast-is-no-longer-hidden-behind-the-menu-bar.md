---
Name: Notifications are no longer hidden behind the menu bar
Category: Fix
Description: Toast notifications — the download-complete confirmation among them — now open in front of the top menu bar instead of behind it.
Icon: Alert
Order: -20260819
---

# Notifications are no longer hidden behind the menu bar

Download a file from a space's **Files** tab and the portal confirms it with a small
notification in the top-right corner. That notification was opening *underneath* the
top menu bar, so depending on your window size you saw a sliver of it or nothing at
all — while the download itself worked perfectly. The same applied to every other
notification the portal raises there: upload finished, delete failed, and so on.

The notification area now sits in front of the menu bar, alongside the other things
that have to appear over it — the chat window, the model and agent pickers, the code
editor's suggestion popups. Nothing else changes: the notifications appear in the
same place, say the same thing, and disappear on the same timer.
