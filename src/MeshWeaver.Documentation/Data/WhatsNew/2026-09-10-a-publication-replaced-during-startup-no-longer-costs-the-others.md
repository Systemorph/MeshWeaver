---
Name: A publication replaced during startup no longer costs the others
Category: Fix
Description: A deployment starting up while one publication is being replaced now keeps the prebuilt content of every other publication, instead of rebuilding all of them.
Icon: DatabaseArrowUp
Order: -20260910
---

# A publication replaced during startup no longer costs the others

Startup reuses the content each publication already built, so a deployment does not have to rebuild
what has not changed. A publication that is being replaced is skipped on purpose: its completion
marker is removed while the new files are uploaded and restored last, and the skipped content is
rebuilt instead.

Until now, a marker that disappeared at the exact moment startup read it stopped that whole reading
rather than just the one publication. Every other publication of the same platform version was
rebuilt too, even though all of them were complete — and a replacement runs several times an hour
while a release is going out.

Startup now skips only the publication whose marker went away and keeps the rest. The log line says
which of the two situations it saw: a publication that is present but not yet marked complete, or
one whose directory has been removed.
