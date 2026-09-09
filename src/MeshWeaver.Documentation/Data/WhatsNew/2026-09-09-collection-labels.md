---
Name: Collection values remain visible in labels
Category: Fix
Description: Labels render collection values consistently instead of failing when they receive an unserialized list.
Icon: TextDescription
Order: -20260909
---

Labels now show list values consistently, including initialization gate names. A locally bound collection uses the same readable text as the equivalent value received over the connection, so rendering it no longer breaks the binding.
