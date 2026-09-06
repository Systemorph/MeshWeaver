---
Name: A fresh install never inherits a repo file's compile verdict
Category: Fix
Description: Creating a type definition from a package or repo file now drops the embedded compile state, exactly as an update already did.
Icon: Sparkle
Order: -20260906
---

# A fresh install never inherits a repo file's compile verdict

Since August, syncing a type definition has kept its authored content and its compile state apart:
exports leave the compile bookkeeping out of the repo file, and an update keeps whatever the live
mesh last recorded. One path was missed. When a type was **created** — a package installed into a
fresh mesh, or a repo imported for the first time — there was no live state to keep, and the file's
embedded verdict was written as the type's initial state instead.

Older repo files still carry such verdicts, and on a fresh mesh they did real damage: a type could
arrive claiming it was compiled against a framework this mesh never ran, pointing at an assembly
that does not exist here, or still asking for a forced rebuild from months ago. The mesh then
rebuilt types it had a perfectly good prebuilt for, adopted a second build over the first, and left
instances of some types bound to nothing — a course exercise that rendered its navigation and no
exercise.

Now a create is treated as an import too. A type definition created from a file starts with no
compile state at all, and the mesh records its own as it compiles or adopts, the same as it always
did after an update. Nothing changes for repo files that are already clean.
