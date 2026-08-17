---
Name: Installs and pushes adopt prebuilt assemblies
Category: Feature
Description: Installing a package or pushing content now checks for a matching pre-built assembly first and adopts it — compilation runs only for content nothing has built yet, and an explicit Compile always rebuilds.
Icon: Sparkle
Order: -20260817
---

# Installs and pushes adopt prebuilt assemblies

Until now, pre-built assemblies were consumed only at server startup: installing a package or
pushing content changes always compiled every affected type on the spot, even when the exact same
build already existed — shipped with the image, published by CI for this very commit, or compiled
by another replica moments earlier.

The rule is now "if a pre-built lib exists, take it; only if not, generate":

- **installing a package** first adopts any matching assemblies from the deployment's bundle
  sources, then only compiles what wasn't covered;
- **pushing content** (git sync) does the same for the affected types — a commit that the content
  repository's CI already baked lands without compiling at all;
- a **compile request that arrives when the current build is already valid** for the current
  sources is satisfied on the spot instead of rebuilding byte-identical output — while an
  explicit *force* compile keeps rebuilding, exactly as documented.

Every adoption is validated first — framework identity and the new per-type dependency record —
so nothing is ever taken on faith; anything that doesn't provably match compiles as before.
