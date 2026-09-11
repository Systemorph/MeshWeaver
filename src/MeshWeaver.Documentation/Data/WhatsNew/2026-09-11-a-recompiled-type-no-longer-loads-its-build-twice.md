---
Name: A recompiled type no longer loads its build twice
Category: Fix
Description: A NodeType compiled on this server kept a second copy of the build its open instances were running; it now keeps one.
Icon: Code
Order: -20260911
---

# A recompiled type no longer loads its build twice

Since this morning's compilation fix, a NodeType compiled on the same server that serves it loaded
each new build twice — once from the compiler's output and once from the assembly store's copy of
the same bytes — and an open instance of the type kept both copies alive until it closed. The
extra copy did not grow with further recompiles, but every build a live instance was running sat
in memory twice. A request for the store's copy now reuses the copy already loaded, so each build
is loaded once, and a recompile releases it as before.

Details: [One build at two paths is ONE generation](/Doc/Architecture/NodeTypeCompilation).
