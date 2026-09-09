---
Name: Assembly cache keeps thirty days of history
Category: Fix
Description: Assembly-cache cleanup preserves at least 30 days of history alongside active generation claims.
Icon: Sparkle
Order: -20260909
---

# Assembly cache keeps thirty days of history

Assembly-cache cleanup now retains unused compiled artifacts for at least 30 days.
Older settings cannot shorten this window. Active generation claims and the current
process continue to protect the files they need.
