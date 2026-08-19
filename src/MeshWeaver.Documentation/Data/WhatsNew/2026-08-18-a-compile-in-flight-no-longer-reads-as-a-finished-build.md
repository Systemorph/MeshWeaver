---
Name: A compile in flight no longer reads as a finished build
Category: Fix
Description: The two gates the package installer orders its root recycle against read a NodeType the way their consumers do, so a just-written type that is still compiling can no longer be mistaken for one with a loadable build.
Icon: ArrowSync
Order: -20260818
---

# A compile in flight no longer reads as a finished build

Installing a package retypes its partition root, then recycles that root so the hub which comes
back binds the package's own configuration instead of the placeholder. The recycle has to wait for
the root's in-package NodeType to actually produce a build — recycle too early and the fresh hub
binds the fallback configuration for its whole lifetime, which is what "No renderer is registered
for area `Tests` on hub `Store`" looks like from the outside.

The two predicates that decide when that wait is over — `HasLoadableBuild` and the settle gate
behind `AwaitCompilationSettled` — asked `node.Content is not NodeTypeDefinition`, a CLR type test
whose "this is not a NodeType at all" escape answers *yes, go ahead*. That escape also fires for a
NodeType node whose content arrived un-materialized, as raw JSON rather than the CLR record — which
is the normal shape for a node that just crossed a sync stream or was **just created**, i.e.
precisely the node the installer wrote a moment earlier. In that shape a type that was still
compiling answered "loadable", and the wait ended before the build existed.

The tell was visible inside a single fold: the installer read the definition with `ContentAs` on
one line and called `HasLoadableBuild` on the next, so the two halves could report "still
compiling" and "loadable" about the same snapshot, and the second one is what settled the wait.
Both predicates now read the definition the way their consumers read it, so a gate and its
consumer can no longer disagree about one node. Same defect and same fix as the instance-side
settle predicate before it.

The emit canary that runs when Roslyn *throws* during a compile got the other half of this
treatment: it now records **where** each of its two probe compiles died, and only reports the
process-level "below Roslyn" verdict when both died in the same frame. Every
`NullReferenceException` in .NET carries the same message, so comparing messages let two unrelated
faults read as one process-wide fault — and that verdict is the one that sends an investigation
after a core dump.
