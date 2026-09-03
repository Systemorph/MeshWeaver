---
Name: A file you just uploaded serves straight away
Category: Fix
Description: A freshly uploaded image or video could answer "service unavailable" for minutes before it started serving; it now serves immediately, even while the upload is still being indexed.
Icon: Sparkle
Order: -20260903
---

# A file you just uploaded serves straight away

Uploading files kicks off background work — each new file is indexed so it becomes searchable and
gets a description. While that was running, asking for one of the files you had just uploaded could
fail: the page waited ten seconds and then reported the file as unavailable, over and over, for as
long as the indexing took. An editor who uploaded a video and previewed it immediately saw a broken
player for minutes, with no way to tell whether the upload had failed or was simply not ready — and
every attempt cost another ten seconds. Then, untouched, it started working.

The two had nothing to do with each other except that they were queued behind one another: reading
a file's location and writing the indexing results were being handled by the same single worker, in
order, so a read had to wait for every write ahead of it. Reads now have a worker of their own. A
file serves as soon as it is uploaded, whatever background work is still in progress — and the same
applies to ordinary page loads during a bulk import or a package installation, which could
previously report content as unavailable for the same reason.
