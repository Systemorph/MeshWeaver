---
Name: A published type now records the same provenance whether or not the build machine had a warm cache
Category: Fix
Description: A compiled node type records what it was built from, and that record is what tells a later installation whether the shipped bytes still match. If the machine doing the building reused an assembly it had compiled earlier, part of that record was silently left out — so two publishes of identical content could ship different amounts of protection. They no longer can.
Icon: Checkmark
Order: -20260910
---

# A published type now records the same provenance whether or not the build machine had a warm cache

When a node type is compiled, the platform records **what the bytes were built from**. That record
is what a later installation checks before it decides to reuse those bytes instead of compiling the
type again — and one part of it, the fingerprint of the exact compilation input, is the part that
can answer the question directly rather than by proxy.

It was only ever recorded when the machine actually ran the compiler. If that machine had built the
same type a moment earlier and reused the assembly it already had — which is the normal, fast case
— the fingerprint was left out. Nothing failed and nothing was logged: the record was still valid,
just weaker, and the check it enables simply never ran for that build.

The consequence is the part worth knowing: **two publishes of byte-identical content could ship
different amounts of protection**, decided by nothing more than whether the build machine happened
to have a warm cache. Nothing downstream could tell which one it had received.

## What changes

**The fingerprint is now written beside the assembly and read back with it.** A build records it
once, at the moment it has it, and any later reuse of those bytes recovers the same value — so the
record is a property of the content, not of the machine or the moment.

**It is the producing build's own fingerprint, not a fresh guess.** The value is read from what was
stored next to the bytes rather than recalculated later. Recalculating would describe the machine
doing the recalculating, which is not the same claim — and getting that wrong would let a build be
reused across exactly the kind of change the check exists to catch.

**An assembly whose fingerprint is missing is rebuilt instead of reused.** Files left by an earlier
version of the platform have no fingerprint beside them, so they are compiled once more. That is a
single rebuild per node type, on the first run after updating, and usually it would have happened
anyway.

## What this does not change

**Nothing about how types are compiled, published or installed.** The bytes, the assemblies and the
bundles are exactly as before. What changed is that the record shipped alongside them no longer
varies with the build machine's state.
