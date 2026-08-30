---
Name: Compile (force) now rebuilds from your source even when a prebuilt assembly still resolves
Category: Fix
Description: A forced Compile used to be silently replaced by the deployment's prebuilt assembly whenever one still resolved for the type — the very bytes you were trying to replace came back, and the node reported a fresh build. A forced release now skips prebuilt adoption and compiles the live source, and the force is spent by that compile so it cannot suppress adoption later.
Icon: Bug
Order: -20260830
---

# Compile (force) now rebuilds from your source even when a prebuilt assembly still resolves

Forcing a release (**Create Release** with force, or `RequestNodeTypeRelease(force: true)`) is the
one verb that is supposed to say "build what is on the node now, whatever you already have". It did
not: the request was honoured, the type flipped to *Pending*, and the compile watcher then asked the
deployment's bundle sources again and adopted whatever still resolved — settling "without a Roslyn
pass" and stamping a fresh success time over the same assembly. Forcing only worked on a type whose
prebuilt had already gone missing, which is exactly where nobody needed it.

This is how a stale prebuilt adopted over freshly synced source could not be pushed off a node whose
code was already fixed (#2813). A forced release now goes straight to the compiler, and the force is
consumed by the compile it dispatches, so an ordinary trigger afterwards is free to adopt a prebuilt
again.

Found alongside it: a type that already had a build could be left stuck at *Pending* when a
prebuilt source reported an adoption it never actually wrote back — its old assembly record made the
adoption look landed. "Landed" now also requires the type to have left *Pending*, so such a type
compiles instead of hanging every page that waits on it.
