---
Name: A missing module build now fails loudly instead of compiling quietly
Category: Fix
Description: Production meshes can require prebuilt module assemblies (Modules:RequirePrebuilt) — a missing or hollow bundle fails the install immediately, naming the package, registry, framework lane and the fix, instead of silently falling back to an on-mesh compile.
Icon: ShieldError
Order: -20260825
---

# A missing module build now fails loudly instead of compiling quietly

Until now, when a module's prebuilt assemblies could not be fetched — the registry did not
advertise the package for this framework build, served no bytes for the lane, or shipped a bundle
with nothing in it — the mesh logged a line and **compiled the module locally**. On a production
portal that fallback hides a distribution failure: the install "succeeds", slowly, and the real
problem (no bundle was ever baked for that lane) surfaces days later as something else entirely.

Deployments can now set `Modules:RequirePrebuilt: true`. On such a mesh, every one of those misses
**fails the install immediately** with one clear message naming the package, the registry, the
framework identity and architecture the bundle is missing for, and the fix — publish or rebake the
bundle for that lane. Nothing compiles. The adoption ledger still records every miss, and an empty
bundle found at boot is reported as an error naming the same fix.

The default is unchanged: meshes that do not opt in — local and dev meshes, CI's disposable
meshes, the bake itself — keep the compile fallback, which remains correct wherever bundles are
not expected to exist.
