---
Name: One dead publish target no longer starves the live ones
Category: Fix
Description: A bake publishes its sealed assemblies to every portal's storage in turn, and a single target that could not be read — a share that no longer existed — stopped the run before the targets after it were written. Each target is now published on its own; the run still fails, naming exactly the targets it could not reach.
Icon: ArrowSplit
Order: -20260830
---

# One dead publish target no longer starves the live ones

When a node repository bakes its NodeType assemblies, the sealed result is published to every
portal's storage share listed for the fleet, one after the other. That loop stopped at the first
target it could not read: a share that had been deleted with a torn-down instance still appeared
in the list, its marker probe answered nothing, and the script — correctly refusing to treat an
unreadable marker as an absent one — exited there. Whether the targets *after* it were published
depended on where in the list the dead entry sat; for three days every bake sealed the two live
shares and then went red on the third, which read as a broken bake rather than a stale list.

Every target is now published in isolation. A target that cannot be read fails on its own, the
remaining targets are still published and sealed, and the run ends red naming exactly the targets
it did not reach. The refusals themselves are unchanged — an unreadable marker is still not an
absent one — only their blast radius shrank from the whole publication to one target. A target that
no longer exists belongs out of the list, and the message says so.
