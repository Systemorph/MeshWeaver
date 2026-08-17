---
Name: The compiler is its own assembly
Category: Feature
Description: The NodeType compile toolchain moved out of the graph into MeshWeaver.Compiler — the framework identity now pins a small, low-churn assembly, so routine platform changes stop invalidating every compiled type.
Icon: Sparkle
Order: -20260817
---

# The compiler is its own assembly

Everything that shapes what a dynamic NodeType compile is fed — the generated provider skeleton,
which source files a type's queries select, `@@`-include resolution, the reference set,
compilation options, source-generator execution, and the emit itself — now lives in a dedicated
`MeshWeaver.Compiler` assembly instead of inside the graph.

Why it matters: a compiled type stays valid exactly as long as nothing it was built from changed.
The identity that decides this used to pin the whole graph assembly — the busiest code in the
platform — so nearly any platform change forced every compiled type to rebuild. It now pins the
small toolchain assembly together with everything the toolchain itself depends on (including the
`#r` directive resolver, whose effect on compiles was previously invisible to the identity), so
rebuilds happen when the toolchain, one of its dependencies, or a public surface actually
changes — and only then.

One pipeline, every path: the portal compiling an edited type on demand, the startup batch bake,
and the CI bake that ships prebuilt assemblies all run the same toolchain code, so what CI built
and what the portal would build can no longer drift.
