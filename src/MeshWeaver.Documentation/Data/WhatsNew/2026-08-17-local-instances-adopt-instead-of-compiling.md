---
Name: Local instances adopt modules instead of compiling them
Category: Feature
Description: A local install now seeds the prebuilt NodeType bundles and reports what they covered, instead of running a full Roslyn bake at every startup — and names any type it could not cover rather than compiling it silently.
Icon: Sparkle
Order: -20260817
---

# Local instances adopt modules instead of compiling them

A laptop is not a build farm. Until now a local install ran the same startup bake a managed portal
does: compile every dynamic NodeType, in-process, at boot. Because the framework identity changes
with every image, that bake started from scratch after every `memex-local update` — one developer
machine had accumulated **15 generations** of assembly cache, each one a full sweep of ~38 types,
none of which produced anything CI had not already built.

Local instances now default to **adopt-only**. The prebuilt bundles seed exactly as before, the
startup then *asks* the assembly store what that covered, and nothing is compiled at boot.

The part worth knowing about is what happens to a type the bundles did **not** cover. It is not
silently compiled, and it is not silently broken: it is **named at every boot**, at warning, with
the bundle sources that were consulted. It still works — it builds on first access, the way any
uncompiled type always has — it is simply not warm. That matters because some content has no CI
bake by construction: a NodeType you are authoring on your own machine has never been through CI and
never will, and "build your own" remains the right answer for it. What the new default refuses is
the *silent* version — a gap you would otherwise discover when a page renders empty.

If you are authoring NodeTypes locally and want the old behaviour back, set
`PreWarm__AdoptOnly: "false"` in your values overlay; the startup bake returns unchanged.

Two related fixes ship with it. `PreWarm__PrebuiltBundleRoot` had been set in the AKS values since
the CI-bake lane shipped, but the chart's configmap never rendered the key — so every
chart-deployed portal quietly ran with the *consuming* half of that lane switched off, recompiling
content CI had already baked for it. The key is now templated, and a guard test asserts that every
`PreWarm__*` key any values file sets is actually rendered, because helm drops an untemplated key
without a word.
