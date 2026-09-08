---
Name: The shared data volume no longer fills up with old builds
Category: Fix
Description: Every CI build left its precompiled bundles on the shared data volume forever — 482 sets, 13 GB — until the volume was full and every compile, upload and deployment on it failed with errors that named nothing. A recurring pass now removes the builds nothing references, and /health says how full the volume is before it matters.
Icon: HardDrive
Order: -20260908
---

# The shared data volume no longer fills up with old builds

Each build of the platform publishes the precompiled content bundles a portal adopts at start-up
instead of compiling. They land on the shared data volume, in one folder per build — and nothing
ever removed one. On the public instance that had reached 482 folders and 13 GB on a 16 GB volume
shared with the installed modules, the compile cache and the sign-in keys, leaving 3 MB free. A
full volume does not say so: writes are cut short silently, so every recompile of a page type
failed as "Bad IL format", the delivery pipeline could not read back what it had just published,
and neither message pointed at the disk.

**A recurring pass now keeps what something references and removes the rest.** What counts as a
reference: the build the running portal is on; every clean release (`3.1.0`, never a `-ci` build)
— those stay available for good, so a rollback always has its bundles; any build a page type on
this instance was built from; the newest build a portal on the same release line would adopt; the
last ten `-ci` builds of a release line that has not shipped yet; and anything that is still being
published. Once a release ships, the `-ci` builds that led up to it are no longer referenced by
that rule and are removed, oldest first, each removal recorded with the space it gave back. A
folder whose state cannot be read is never removed.

The pass runs shortly after each portal start, once the start-up compile has settled, and daily
after that; a ledger on the volume records what each pass did.

**`/health` now reports the volume's free space.** When the volume holding the bundles or the
installed modules has less than 1 GB free, the health endpoint reads `Degraded` and names the path,
how much is used and how much there is — before the next write fails. The portal keeps serving; the
signal is for whoever watches it.

See [CI Content Bake](/Doc/Architecture/CiContentBake) for the rule and its configuration.
