---
Name: Bake publication no longer spends minutes per target
Category: Fix
Description: Publishing a CI content bake to the portals' shared storage launched one command-line process per file to upload and another per file to verify — 184 launches for two targets, 5–10 minutes of every bake. Each phase is now one process per target, every file still verified; the publication step takes seconds.
Icon: Timer
Order: -20260908
---

# Bake publication no longer spends minutes per target

Every merge to `main` bakes the platform's shipped content inside the image that ships it and
publishes the result — about 46 files — to the storage every portal mounts, so a booting pod adopts
the compiled bundles instead of compiling them itself. Before writing the seal that makes a
publication visible, the publisher reads every file back and checks that the bytes on the shelf are
the ones it uploaded, because two publishers can land on one directory at once and a seal over a
mix is silent and wrong.

The upload and that read-back were each done with one command-line process **per file**, per
target. For two targets that was 184 process launches, each paying the tool's start-up, a token
lookup and a new connection for a single request — 5 to 10 minutes of a bake whose actual compile
takes about 7. The maintainer's question, on seeing the bake queue crawl: *"how many are there? this
must be one bulk query"*.

**Each phase is now one process per target.** The upload pushes every file through a bounded pool
with the same two stamps as before; the read-back lists the destination once and reads every file's
stamps over one connection in the same process, printing one row per file that the existing
accounting consumes. Nothing about the verdict changed: every file is still verified (never a
sample), both stamps of every file are still read, an unreadable file still refuses the seal, a mixed
directory still has its foreign seal removed, and the seal is still written strictly last. Measured
on the publisher's own test harness, about twenty-five full publications now complete in 25 seconds.

See [Sealed Publication Reads](/Doc/Architecture/SealedPublicationReads) → "What the postcondition
costs" for the before/after table.
