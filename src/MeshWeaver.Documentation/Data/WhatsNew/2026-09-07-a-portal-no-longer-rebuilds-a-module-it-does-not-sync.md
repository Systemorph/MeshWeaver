---
Name: A portal no longer rebuilds a module it does not sync — it tells you
Category: Fix
Description: When a module's prebuilt build is missing or refused on a partition your portal does not sync from a repository, the module now shows a clear error naming the fix, instead of silently compiling an old copy of its files and serving that as current.
Icon: Checkmark
Order: -20260907
---

# A portal no longer rebuilds a module it does not sync — it tells you

Modules arrive on your portal as prebuilt bundles. When a bundle could not be used — it was built
from newer files than your portal holds, or no bundle for your portal's current platform build had
landed yet — the portal used to fall back to compiling the module's files itself. On a partition
your portal syncs from the module's repository that is fine: the files are current. On a partition
nothing syncs, the files are whatever an install left behind, and the portal was quietly building
old code and serving it as if it were current. Nothing on screen said so.

Now the portal refuses to do that. The module's pages show a compilation error that names the
partition, says whether the bundle was declined or simply absent, and lists the two ways to fix it:
add a sync source for the partition (Partition Sync administration) and sync it, or publish/rebake
the module for this platform build — then request a release to retry. Content you author yourself
is not affected, and a partition that does sync from a repository keeps working exactly as before.
