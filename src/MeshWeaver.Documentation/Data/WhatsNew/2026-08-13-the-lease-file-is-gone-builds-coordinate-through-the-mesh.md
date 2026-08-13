---
Name: The lease file is gone — builds coordinate through the mesh
Category: Feature
Description: Build coordination through the Build nodes is now the default everywhere; the file lease that used to decide who bakes is deleted, and waiting servers subscribe to the GO signal instead of polling.
Icon: Sparkle
Order: -20260813
---

# The lease file is gone — builds coordinate through the mesh

After its first production run coordinated a full gated build cleanly, the build protocol is now
the default on every deployment — no flag needed. The file lease that used to decide which server
bakes is deleted outright: its one-builder guarantee and its "a dead builder is superseded
automatically" property both live on in the build root's claim, where they are ordinary,
observable mesh state instead of a hidden file on a shared volume.

Servers that are not building no longer poll the shared cache every minute to see whether the
builder finished — they subscribe to the build's GO signal and complete the moment it lands.

The escape hatch remains for hosts with nothing to coordinate: switching the protocol off simply
bakes solo, which is the right shape for a single-process setup and the wrong one for any fleet.
