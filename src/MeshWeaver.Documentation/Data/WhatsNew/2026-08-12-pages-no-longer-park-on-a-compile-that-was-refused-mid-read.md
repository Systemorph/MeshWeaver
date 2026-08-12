---
Name: Pages no longer park on a compile that was refused mid-read
Category: Fix
Description: A node type that pulls in code from a neighbouring node now compiles even when the compile runs in the background with no signed-in user, instead of failing and leaving the page stuck.
Icon: Sparkle
Order: -20260812
---

# Pages no longer park on a compile that was refused mid-read

When a node type reuses code from a neighbouring node, the compiler has to go and fetch that
neighbour. That fetch was being made on the compiler's behalf without an identity attached — the
platform quite correctly refuses an unattributed read, and the refusal came back looking exactly
like "the neighbour does not exist". The reference was then left in the code as written, so the
compile failed on the reference line itself and the node type parked with an error. Every page
served by that type waited on the parked type before giving up.

A compile reading the source it was asked to compile is platform work, not something done on any
one person's behalf, so those reads now run under the platform's own identity — the same way the
compiler already discovers which files belong to a type. The same change applies to the compiler's
second route for finding source files, which could previously see a smaller set of files than the
first and report missing types that were in fact present.
