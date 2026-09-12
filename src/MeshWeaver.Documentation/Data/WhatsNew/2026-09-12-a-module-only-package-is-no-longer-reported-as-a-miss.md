---
Name: A module-only package is no longer reported as a miss
Category: Fix
Description: A package that ships a module and declares no NodeTypes has nothing to adopt — and was being counted as content the registry failed to serve, holding the bundle_adoption health check at Degraded on portals with nothing wrong with them, and failing installs outright on a require-prebuilt mesh. It is now its own outcome, decided from what the bundle declares rather than from what it lacks.
Icon: Checkmark
Order: -20260912
---

# A module-only package is no longer reported as a miss

A prebuilt bundle carries the compiled assemblies for a package's **NodeTypes**. Plenty of packages
have none: `AI`, `Anthropic`, `Maps`, `Chat`, `Mcp`, `Notifications` and a dozen others ship a
**module**, which lands by an entirely separate path. Their bundle is complete, correct, and empty
of assemblies by design.

Until now the portal read that emptiness as a failure. Every one of those packages was recorded as a
**miss** — rendered on `/health` as *"content the registry was meant to serve is compiled here
instead"* — when nothing had been missed and nothing was being compiled in its place.

Measured on memex.systemorph.com on 2026-09-12, immediately after the identity-selection fix made
the underlying state visible:

```
bundle_adoption: Degraded — 25 adoption attempt(s), 25 adopted, 13 MISS(es) …
  AI: NoAssemblies (the bundle carried no assemblies); Anthropic: NoAssemblies; Maps: … (+10)
```

All thirteen had landed correctly. The portal was healthy and its health check could not say so.

Two things follow from that, beyond the wording:

- **`bundle_adoption` was Degraded permanently** on a portal with nothing wrong with it — and it is
  the instrument the delivery issues are triaged with, so the false reading was being quoted as
  evidence in other investigations.
- **On a `Modules:RequirePrebuilt` mesh the install failed.** That flag exists to forbid a silent
  fallback to compiling; a package that compiles nothing has no fallback to forbid, but it was
  routed through the same refusal and threw.

An empty bundle is now classified from a **positive declaration** — the package says it ships a
module, or content — and never from the absence of assemblies. So the benign reading is unreachable
by accident: a producer that ships a genuinely empty archive, declaring nothing at all, is still
reported as a miss and still stays loud. Unresolved types the bake could not produce still dominate
everything else, as before.

The rendered line keeps its denominator, because *"25 attempts, 25 adopted, no misses"* would invite
you to conclude that twenty-five packages were served:

```
25 adoption attempt(s), 25 assembly/assemblies adopted, no misses (13 carried no NodeTypes to adopt)
```

The four independent stages between a merge and a portal serving prebuilt bytes — and which
instrument answers for each, so a reading from one is not mistaken for evidence about another — are
written up under [Bundle Delivery Stages](/Doc/Architecture/BundleDeliveryStages).
