---
Name: Compiling a type scans its sources once, not three times
Category: What's New
Description: Type compiles now take a single source snapshot and reuse it, cutting the mesh reads a compile costs on a large portal.
Icon: Sparkle
---

# Compiling a type scans its sources once, not three times

Every time the platform compiled one of your types, it went looking for that type's source files
three separate times over: once to work out whether the cached build was still valid, once to
actually feed the compiler, and once more to record what had been built. On a small workspace
those extra lookups are free, which is why nobody noticed them; on a large portal with many
partitions each one is a real query, and a compile ran them all while the portal was busy serving
everything else.

A compile now takes that snapshot once and hands the same one to every step. The compile activity
page reports it plainly — a single "source snapshot" line saying it was taken once and reused,
instead of the discovery phase running again further down.

It also removes a way those three lookups could quietly disagree. Each of them read a live,
changing list, so the cache decision, the code that actually got compiled, and the record of what
was compiled could describe three slightly different sets of files — which could make the platform
skip a rebuild it owed you, or run one it did not. One snapshot cannot disagree with itself.
