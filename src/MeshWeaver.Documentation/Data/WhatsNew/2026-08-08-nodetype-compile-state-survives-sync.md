---
Name: A sync or install no longer reverts a type's compile state
Category: What's New
Description: Importing, syncing or installing a NodeType keeps whatever the mesh most recently compiled, instead of stamping an older verdict back over it.
Icon: Sparkle
---

# A sync or install no longer reverts a type's compile state

A NodeType's compile state — whether it built, which assembly it built, which sources it was built
from — belongs to the mesh that ran the compile. Everything else about the type (its configuration,
its sources, its description) belongs to whoever authors it: a git repository, a plugin package, or
you.

Until now a repository import, a plugin install or an instance sync could carry its own older copy
of that compile state along with a genuine edit, and the older copy won. A type that had just been
rebuilt would appear to fall back to its previous state, sometimes pointing at a release that no
longer existed, and the only way out was to compile it again by hand. A copy that claimed success
for a build that was months old was worse: the type looked healthy right up until something actually
needed the assembly.

Writers no longer decide this. Whatever the mesh last recorded for a type is what the type keeps,
and an authored change lands beside it. Re-importing a repository that has not changed anything now
also leaves such types completely untouched, so they stop being flagged as locally edited when
nobody edited them.
