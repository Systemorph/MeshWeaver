---
Name: Values no longer render empty after an update
Category: Fix
Description: A page could come back blank after a portal update — not because its data was lost, but because a lookup that ran out of time was mistaken for a definite answer.
Icon: Sparkle
Order: -20260810
---

# Values no longer render empty after an update

After a portal update, an occasional page came back empty. Its fields showed nothing, tools reading
the same item answered "unavailable", and anything waiting for the value simply timed out. The data
was never gone: opening the item's contents directly still showed everything, filled in.

Every portal update rebuilds the custom item types the portal serves, and they are rebuilt one after
another, which can take several minutes. During that window the portal asks, for each item, "which
type is this?" — and that question is given a short deadline so a genuinely unknown type is reported
quickly instead of stalling the page. The mistake was treating a deadline that ran out as if it were
an answer. Under the rebuild the question sometimes could not be answered in time, and the portal
concluded the type was unusable, then served the item without it. With no type in hand it could not
read the item's own fields, so they came back blank — and, because that conclusion was reached once
and kept, the page stayed blank long after the type had finished rebuilding and was working
perfectly. Since it depended on timing, it hit some servers and not others, which made it look
arbitrary.

Now an unanswered lookup is treated as what it is: not yet known, rather than known to be bad. The
portal waits for the type to actually appear — which it does as soon as its rebuild finishes — and
then serves the item with its fields intact. A type that genuinely is not registered is still
reported straight away, and if a type never turns up at all the page explains that it is a lookup
problem rather than telling you to fix code that was never at fault.
