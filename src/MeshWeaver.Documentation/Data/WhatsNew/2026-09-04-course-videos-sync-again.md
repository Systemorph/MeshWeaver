---
Name: Course videos sync again
Category: Fix
Description: A content file too large to ride a message now travels through the content store instead — so the 25 videos that could not reach any Education Space are there, and a Space that used to report "no content" reports what actually happened.
Icon: Bug
Order: -20260904
---

# Course videos sync again

A Space's `content/**` files — course videos, posters, PDFs — are mirrored into the mesh by the
GitSync import. Until now the bytes travelled **on the message**, and every transport under the mesh
puts a ceiling on how large one message may be. A file bigger than that ceiling was refused, every
time, on every attempt.

That was not an edge case. On `MeshWeaver.Education` it is **25 files across all seven Spaces** — and
the axis is *"has a video"*, not *"is large"*: the smallest Space in the repo carries the
second-largest single file. A learner opening one of those pages saw a video that never loaded.

**A file that cannot fit a message now goes around it.** Its bytes are written into the destination
collection once, and the message carries a short, content-addressed reference to them; the receiving
Space streams the bytes into place. Nothing else about a content sync changed — the mirror still
prunes exactly what the source dropped, a file a user uploaded is still preserved, and a sync that
already fitted is byte-for-byte the request it always was.

Two things follow from the reference being content-addressed:

- **Re-running an import duplicates nothing.** The same bytes address the same place, so a second run
  is a no-op rather than a second copy.
- **Nothing is left lying in the store.** The import owns the bytes it parks and clears them before
  it answers, so the folder is clean the moment the sync reports done.

**A transfer that fails still says so.** If the bytes cannot be parked — a Space whose store the
import cannot reach — the file falls back to the old road and the failure names *both* halves: which
file is over the limit and by how much, and why the new road was unavailable. And a reference the
receiving Space cannot resolve is reported as the failure it is, never as a file quietly written
empty. Each Space's content-sync log keeps telling you the truth about its own assets.
