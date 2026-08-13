---
Name: Version history no longer collects edits nobody made
Category: Fix
Description: When a page was changed from somewhere else — another server, a sync, a repair job — the page picked the change up but also recorded it a second time under a brand-new version number, adding an entry to the history for an edit that never happened. Picking up someone else's change is now just that.
Icon: History
Order: -20260813
---

# Version history no longer collects edits nobody made

A page can be changed from more than one place: another server in the same deployment, a
repository sync, a background repair job. Whichever server is currently serving the page
notices the change and takes it on, so everyone keeps seeing the same content. That part
worked.

What it also did was treat taking the change on as an edit of its own. It gave the content a
fresh version number one above the one that had just been saved, and then saved that back —
so the stored page ended up one version ahead of anything anybody had actually written, and
the version history grew an entry whose contents were identical to the one before it. Every
time two places converged on the same page, the history collected another such entry, and
for a brief moment the page being served claimed a version that existed nowhere.

Picking up a change someone else made is now treated as what it is — reading, not writing.
The content is adopted exactly as it was saved, keeps the version it was saved under, and
nothing is written back. Genuine edits are unaffected and still get their own version, and a
change that arrives late is still ignored rather than being allowed to undo newer content.
