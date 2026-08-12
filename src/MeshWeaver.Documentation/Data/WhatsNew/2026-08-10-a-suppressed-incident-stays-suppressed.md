---
Name: A suppressed incident stays suppressed
Category: Fix
Order: -20260810
Description: Asking an incident to be suppressed while it was mid-filing no longer gets overwritten on the next occurrence.
Icon: Sparkle
---

An incident that was in the middle of opening its GitHub issue had an automatic recovery
step that re-asked it to file. That recovery ran whenever the incident was in that state —
including when someone had just asked for something else, such as suppressing it. Because a
noisy fault reports over and over, the request was overwritten each time and never got the
chance to take effect, so the incident filed itself anyway.

The recovery now only steps in when nothing else has been asked for, which is exactly the
situation it was written for. An instruction you give an incident is now respected.
