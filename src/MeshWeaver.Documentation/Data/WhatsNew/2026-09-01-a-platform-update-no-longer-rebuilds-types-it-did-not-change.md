---
Name: A platform update no longer rebuilds types it did not change
Category: Fix
Description: When an update leaves a type's code untouched, the portal now checks that instead of assuming the worst — and it catches the opposite case too, where the code did change and nothing noticed.
Icon: Sparkle
Order: -20260901
---

# A platform update no longer rebuilds types it did not change

Every type you author in the mesh is compiled, and the platform has to decide, after each update,
whether what it compiled earlier is still good. Until now that decision leaned on a stand-in: *the
tools that do the compiling changed, so anything they produced might be out of date.* True, and far
too broad — the set of things that counts as "the tools" moves on most changes to the platform, so
an update that touched none of your code still declared compiled types suspect and rebuilt them.

The platform already recorded, on every compile, a precise summary of exactly what went into it.
Nothing read it back. That summary is now read.

When a type is about to be rebuilt, the portal first reconstructs what a fresh compile would be
handed and compares it with what the existing build was made from. If they are identical, the
existing build is kept and its record simply brought up to date — no rebuild. If they differ, it
rebuilds, as before.

**The check also runs the other way, and that is the part you may actually have felt.** Some changes
to your code moved what gets compiled without moving anything the old checks looked at — most
visibly, editing a snippet that another file pulls in by reference. That edit could go unnoticed and
the type would carry on running the older code. It is now noticed, and the type is rebuilt.

If the comparison cannot be made — an older build with no recorded summary, or sources that cannot
be read at that moment — nothing is assumed. The platform behaves exactly as it did before and
rebuilds. A needless rebuild costs a little time; keeping code that has moved on is the one outcome
that is never acceptable, so every uncertain case takes the safe side.

One case is deliberately left out for now. A build made under a *previous* platform release is still
rebuilt even when its inputs are unchanged: those files are stored per release, and reusing one
across releases is exactly how a portal can be brought down. The reasoning, and what would be needed
to close it safely, is written up in
[The Toolchain Re-evaluation Lane](/Doc/Architecture/ToolchainReevaluationLane).
