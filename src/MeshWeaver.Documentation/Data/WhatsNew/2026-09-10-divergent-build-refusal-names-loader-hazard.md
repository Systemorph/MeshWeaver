---
Name: Divergent module builds report the loader hazard accurately
Category: Fix
Description: A rejected module publication now explains the load-order hazard that remains after dependency records became version floors, instead of claiming every duplicate build causes a NodeType adoption failure.
Icon: Warning
Order: -20260910
---

# Divergent module builds report the loader hazard accurately

The publication gate still refuses two different builds of the same assembly in one sealed module
set. That refusal is necessary because the runtime loads only the first copy it encounters; code
compiled against the other copy silently runs the winner.

Its diagnostic still named a different consequence: it said the losing copy's NodeTypes would be
declined at adoption. Dependency records now state a version floor, so same-version rebuilds remain
adoptable even though the loader hazard is unchanged.

The gate now names the surviving hazard and explains why the version floor does not make two builds
safe. Its regression test checks that both release lanes keep that explanation while still refusing
the divergent publication and naming both producers.

This fixes [#3962](https://github.com/Systemorph/MeshWeaver/issues/3962).
