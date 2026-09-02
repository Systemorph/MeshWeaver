---
Name: Course videos and images sync again
Category: Fix
Description: A space whose repository carried more than a few megabytes of videos, images or other files could no longer sync them — the sync was sent as one enormous message that either exhausted a portal's memory or was rejected outright. Files are now written in batches sized to what the platform can carry.
Icon: ArrowSync
Order: -20260902
---

# Course videos and images sync again

When a space is synced from a git repository, the files committed under its `content/` folder —
course videos, posters, images, fonts, anything that is not text — are copied into the space so the
portal can serve them. That copy was sent as **one message carrying every file in the space at
once**.

For a space with a handful of small images, that worked and nobody noticed. For a course with video
in it, it did not. One space in the education repository holds 28 MB of video across ten files;
another holds 106 MB across twelve. Packaged for transport, those become roughly 38 MB and 141 MB —
and a message is copied and re-encoded several times on its way, each copy costing several times
its own size again.

The two ends of that produced two different failures, both silent from the outside:

- **The 28 MB space exhausted the portal's memory** while packaging the message. This happened on
  a live portal on 31 August and again on 2 September. The sync failed; nothing said so.
- **The 141 MB space was simply too large to send at all.** Recent work added a check that refuses
  a message bigger than the transport can carry — correctly — so that space's videos stopped
  syncing entirely rather than taking a portal down. Also silently.

**What changed.** The sync no longer builds one message. It writes the files in batches sized to
what the platform can comfortably carry, one batch at a time, so the cost of a sync no longer grows
with the amount of content a space holds. A single very large file still travels on its own, which
is far cheaper than travelling with everything else.

The part that deletes files the repository no longer carries still happens exactly once, and still
measures the folder against the **complete** set of files being synced — so nothing is deleted
because it happened to travel in a different batch, and, as before, a file you uploaded yourself
that the repository never tracked is left alone.

What you should notice: syncing a space with videos or large images in it now works, and a big
sync no longer competes with everything else the portal is doing. Spaces small enough to have been
working all along behave exactly as they did.
