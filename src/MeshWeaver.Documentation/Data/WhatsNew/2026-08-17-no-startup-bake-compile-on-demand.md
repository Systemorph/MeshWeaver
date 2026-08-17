---
Name: No startup bake anywhere — modules arrive pre-built, the rest compiles on demand
Category: Feature
Description: Every instance now adopts the assemblies CI already built and compiles nothing at startup; whatever is left over builds the moment a user first reaches it, and the boot log names exactly what that is.
Icon: Sparkle
Order: -20260817
---

# No startup bake anywhere — modules arrive pre-built, the rest compiles on demand

Portals used to compile every dynamic NodeType at startup. That made sense when nothing arrived
pre-built. It stopped making sense once the content repos began publishing their compiled
assemblies: a recent production boot compiled **nothing at all** — every one of its 84 types was
already built — and still spent half a minute doing the sweep that discovered this.

Startup now does the useful half and skips the rest:

- **Adopting the pre-built assemblies is unconditional.** Every instance seeds what CI built for it,
  every boot. This used to sit behind the same switch as the compiler, which had an unfortunate
  consequence: the *default* configuration turned both off together, so an ordinary deployment
  adopted nothing and quietly rebuilt content that was sitting pre-built in its own image.
- **Nothing is compiled at startup.** Whatever the adoption did not cover compiles the first time
  someone actually opens it — about two seconds, once, for that page's first visitor, instead of a
  startup sweep over everything whether anyone wants it or not.
- **The gap is named, every boot.** The log lists each type that did not arrive pre-built and why,
  so "our modules ship pre-compiled" is a claim you can check rather than hope for.

The practical effect is that starting a portal is fast and predictable, and a laptop stops burning
its CPU rebuilding modules that were already built for it.

**If you author NodeTypes locally** there is nothing to adopt — your own code has never been through
CI, by construction — so it compiles when you open it, exactly as before. To get the old
build-everything-at-startup behaviour back, set `PreWarm__DynamicTypes: "true"` in your values
overlay.

One trade worth stating plainly: because nothing compiles at startup, a NodeType that breaks against
a new version is no longer caught while that version is rolling out — it surfaces when someone opens
it. The startup coverage report is what replaces that early warning, and it fires on the first
instance of a bad rollout rather than on the first user.
