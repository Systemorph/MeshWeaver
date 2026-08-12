---
Name: Platform updates keep using the proven preparation path
Category: Fix
Description: The new batched preparation is now opt-in after it misread content at production scale; updates use the proven path by default.
Icon: Sparkle
Order: -20260811
---

# Platform updates keep using the proven preparation path

A new, much faster way of preparing content types during a platform update was introduced earlier
today. Its first run against a large production mesh read only part of the available source
content, and reported many healthy types as broken. Nothing was lost and no page changed — the
update safety-check spotted it and kept the previous version serving — but the fast path is not
trustworthy yet.

Updates now use the proven preparation path by default, and the faster one has to be switched on
deliberately. The speed improvements that did hold up — no more artificial pauses between types
during an update — remain in place.
