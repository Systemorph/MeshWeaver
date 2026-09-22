---
Name: A Space left holding two half-trees now notices, says so, and repairs itself
Category: Fix
Description: When two writers each updated part of a Space, it could be left holding one file from each of two versions — the symptom being a page that stops rendering with an error naming a function its own code defines. The portal now detects that, names the Space and both writers, and re-imports the whole Space instead of leaving it mixed.
Icon: LockClosed
Order: -20260917
---

# A Space left holding two half-trees now notices, says so, and repairs itself

A Space whose content comes from a repository is normally updated in small steps: only the files
that changed since the last update are written. That is right while the Space still matches the
version it was last updated to — and wrong the moment something else has written part of it, because
the files that did not change upstream are then left exactly as the other writer left them.

The result is a Space holding one file from each of two versions. It is easy to miss, because every
record reads as healthy: the Space says it was imported successfully, the install record says it
installed successfully, and both are telling the truth about their own half. What a reader sees
instead is a page that stops working, with an error naming a function that the Space's own code
plainly defines — because the page and that function came from two different versions.

Two Spaces on one portal were in that state for over a day.

The portal now compares what a Space actually holds against the version it says it holds, every time
a new build arrives. When the two disagree it re-imports the whole Space rather than another
increment, and it writes a line naming the Space, the repository and version it believed it was at,
the specific types whose files do not match, and where to find the other writer.

A related check covers the same mix reached from the other direction: an automatic install into a
Space that is kept current from a *different* repository is now held back and recorded, instead of
adding a second version's files to it. An install into the Space's own repository is unaffected.
