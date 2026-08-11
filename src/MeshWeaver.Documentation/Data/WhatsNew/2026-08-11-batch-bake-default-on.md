---
Name: Every platform update now prepares in about a minute
Category: Feature
Description: The fast batched preparation is now the default for updates everywhere, after running clean on all three portals.
Icon: Sparkle
Order: -20260811
---

# Every platform update now prepares in about a minute

Earlier today the faster way of preparing content types during a platform update was switched off by
default, after its first run at full production scale read only part of the available content. The
two causes behind that have since been found and fixed, and the preparation step now refuses to
answer at all rather than guess when it cannot see everything.

It has since run clean on every portal — a full fleet of 237 content types prepared in about a
minute, where the previous approach took nearly nineteen minutes. So it is the default again for any
deployment that waits for preparation before serving, and the setting that controls it goes back to
being an override for the rare case that needs the older, slower path.
