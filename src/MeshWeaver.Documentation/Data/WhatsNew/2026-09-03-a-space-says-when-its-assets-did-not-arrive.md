---
Name: A Space now says when its assets did not arrive
Category: Fix
Description: When a Space's images and videos are too large to be delivered, the Space itself now says so — naming the file, its size and the limit — instead of looking exactly like a Space that has no assets at all.
Icon: Sparkle
Order: -20260903
---

# A Space now says when its assets did not arrive

Files you commit alongside a Space — course videos, posters, images — travel to the platform as part
of the sync. Some of them are simply too large for one delivery, and the platform correctly declines
to send those rather than damaging the connection carrying them.

Until now it declined **in silence**. A Space whose assets had been turned away reported exactly
what a Space with no assets reports: nothing. The sync recorded a clean result, later syncs skipped
the Space because the last one looked fine, and the first person to notice was a reader opening a
page whose video would not play.

Two things change.

**Your Space tells you.** A Space whose assets did not arrive now carries a warning of its own, on
the Space, saying that its assets are not in the platform and why. When a later sync does deliver
them, the entry turns green — so the warning is worth believing, and it disappears on its own once
the problem is fixed.

**The message names the file.** Instead of a guess at the cause, the report gives the file that is
too large, how large it is, and the limit it exceeds — for example *"videos/module1-intro.mp4 at
13,188,820 bytes, 12.6× the per-delivery budget"*. Refusals that have nothing to do with size say
what they actually were, rather than being reported as a size problem.

The sync also no longer records such a run as fully successful, so the next sync tries again instead
of skipping the Space on the strength of an earlier clean result.
