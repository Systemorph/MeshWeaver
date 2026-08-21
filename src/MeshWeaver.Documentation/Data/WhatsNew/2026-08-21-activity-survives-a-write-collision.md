---
Name: Your activity is recorded even when two pages update it at once
Category: Fix
Description: A visit that collided with another update to the same record used to be dropped; the record is now re-read and the visit applied.
Icon: Sparkle
Order: -20260821
---

# Your activity is recorded even when two pages update it at once

Every page you open updates one small record of what you have looked at and when. When two of those updates arrived at the same moment, one of them was refused — correctly, because it had been written against a version that had already moved on — and then thrown away. The visit was simply not recorded. Nothing failed visibly, which is the awkward part: the history looked complete while occasionally missing an entry.

A refused update now waits for the newer version of the record to arrive, re-reads it, and applies the visit on top. The count and the "last opened" time come out right whether one page updated the record or several did at once.
