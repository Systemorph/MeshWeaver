---
Name: A release says where its assemblies are
Category: Feature
Description: A Release node now records, per framework identity and per architecture, where its compiled assemblies live — so an installation resolves the build it can actually run instead of being told only "not adoptable".
Icon: Sparkle
Order: -20260817
---

# A release says where its assemblies are

Installing a plugin should not cost a compile. It only avoids one when the installation can find
assemblies that were built for exactly the framework build it runs — and until now the only way to
look was an index describing whatever the serving instance happened to have installed. A release
itself said nothing about where its bytes were.

It does now. A `Release` node carries the link: for each build lane, the framework identity those
assemblies were compiled against, the architecture that produced them, and where they live.
Resolution becomes "read the release, follow its link" — a property of the release, not a list to
poll — and the download route takes the asking installation's own lane, so it is served the build it
can run rather than the one the registry happens to prefer.

**Why the architecture is written down.** The framework identity is the compatibility proof, and it
already differs between the x64 and arm64 halves of one image — but it is an opaque hash. An arm64
installation reading an x64-baked registry could therefore only ever be told "not adoptable", which
reads identically to an incompatible framework and hid the real answer: no arm64 build had been
published. Naming the architecture beside the identity turns that into a sentence you can act on.

Nothing became more permissive. The identity is still matched exactly, there is still no
"near enough" fallback, and a lane that cannot be proven simply compiles — which always works. What
changed is that the misses are now counted and named: a bundle that arrives with fewer assemblies
than the package has types says so, at both ends, instead of quietly looking complete.
