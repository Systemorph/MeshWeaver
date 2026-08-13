---
Name: A damaged compiled assembly no longer parks a node type
Category: Fix
Description: A freshly compiled node type is published only once the file on disk is proven to be the assembly that was compiled, so a lost or partial write is recompiled instead of permanently disabling the type.
Icon: Sparkle
Order: -20260813
---

# A damaged compiled assembly no longer parks a node type

When a node type is compiled, the resulting assembly is written to the compilation cache and then
loaded to discover the layout areas and content types it provides. Until now the only check before
that file became visible to readers was that it existed and was not empty — so a file that was
short, or the right size but missing a chunk in the middle, was published and loaded anyway.

That failure was permanent rather than temporary. Loading a damaged assembly is recorded as a
compilation error, and a node type that has already failed once is never rebuilt on its own, so a
single bad write left the type showing a compilation error until someone rebuilt it by hand. A node
type stuck in that state also holds up the portal becoming ready.

The compiler now records a fingerprint of the assembly it produced and refuses to publish the file
unless the bytes on disk match it, recompiling instead. If the artifact still cannot be written
correctly after three attempts the compile fails with a message naming exactly what was wrong,
rather than silently disabling the type.
