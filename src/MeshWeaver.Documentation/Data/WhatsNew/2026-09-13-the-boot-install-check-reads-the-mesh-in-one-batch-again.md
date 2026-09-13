---
Name: The boot install check reads the mesh in one batch again
Category: Fix
Description: The completeness sweep that runs at startup no longer reports healthy nodes as ABSENT when a partition's storage could not be read — it now says the mesh could not be read, which is not a pass.
Icon: DocumentSearch
Order: -20260913
---

# The boot install check reads the mesh in one batch again

Every boot, each installed package is checked against the mesh: the install record lists the nodes
the package wrote, and the sweep reads them back. The rule it follows is written into the code that
does it — read the whole declared set in ONE batch, never one probe per path, because a probe for a
path that may not be there is the shape that trips a storm breaker on the node's owning hub.

The rule was not what ran. The storage facade every portal actually uses did not implement the
batched read, so it fell back to the framework default — one probe per path, all at once, for every
batched read in the platform. A backend that really can read a hundred paths in one query was
never asked to.

**The interesting part is not the extra round-trips; it is that the two reads do not fail the same
way.** A single-path read against Postgres treats a partition whose table was never created as an
answer: *no node here*. The batched read treats it as what it is: *this store could not be read*.
Under the fallback, therefore, "storage is not there" and "the node is gone" arrived as the same
answer — and the sweep turned it into an error naming perfectly healthy nodes as ABSENT, on every
boot, for a package that was fine. Nodes under a `Source/` or `Test/` path are stored separately
from their siblings, so they could meet this on their own while the other two hundred nodes of the
same package read back normally.

The facade now forwards the batch: providers are tried in order, the first that holds a path serves
it, and each next provider is asked only for what is still unanswered. A store that cannot answer
now faults instead of shrugging, and the sweep reports the honest verdict — *the mesh could not be
read, so completeness was not checked, and this is not a pass* — rather than a list of missing
nodes that are not missing.
