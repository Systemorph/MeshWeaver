---
Name: The content bake is a build step again, and the gate judges what ships
Category: Feature
Description: CI now compiles shipped content without standing up a mesh, and the mesh that checks it renders and tests the very assemblies that will be published.
Icon: TopSpeed
Order: -20260822
---

# The content bake is a build step again, and the gate judges what ships

Producing an assembly and proving it works are two different jobs, and until now
one command did both: the platform's CI bake stood up an in-process mesh,
imported the shipped documentation content, let that mesh compile every NodeType,
and collected whatever it produced. It was a headless mesh rather than a serving
portal, but it was still the runtime doing the compiler's work — mesh startup,
the hub scheduler, and one activation per type, on every release.

Those two jobs are now separate.

**The bake is a build step.** `mw-compiler compile <root> --output <dir>` reads
the node tree, compiles it with the extracted toolchain and writes the same
bundles as before. There is no mesh anywhere in its path — no builder, no import,
no hubs — and that is asserted structurally rather than believed: a test walks the
call graph of the shipped binary and fails if the bake reaches anything that
builds a mesh, with the gate's own path as its control.

**The gate consumes that bake.** The mesh still runs, because rendering a page and
executing a `Tests` area are genuine runtime behaviours — but it now *adopts* the
baked assemblies instead of building its own. So the bytes that render, and the
bytes whose tests run, are the bytes that will actually be published. Previously
the release gate proved that a private recompile of the same sources worked, and
then shipped different ones.

Two things are refused rather than tolerated, because both would otherwise pass
silently: a bake addressed to a different framework build than the one checking
it (every assembly would be quietly declined and the gate would compile the tree
itself, green), and a run that consumed less of the bake than the bake offered.

Along the way this uncovered a race worth naming: adopting a pre-built assembly
has to write the node it belongs to, and that write woke the very machinery that
starts a first compile — so an adoption regularly finished, correctly, and was
then overwritten milliseconds later by a compile it had been trying to avoid.
Every log line said the adoption had worked, because it had. Adoptions now hold
that machinery off until they are done, which means pre-built assemblies are
actually used when content is installed — on a running instance as much as in CI.
