---
Name: Production converges onto new builds by itself
Category: Fix
Description: A deployment can now opt in (Modules:AutoRecycleOnStaleBuild) to have every page automatically pick up a freshly compiled module build — no more "a newer build is available" banners lingering on production while pages serve the previous code.
Icon: ArrowSync
Order: -20260825
---

# Production converges onto new builds by itself

When a module updates on a live mesh, its types recompile green — but until now every page that
was already running kept executing the **previous** build behind a *"a newer build of this type is
available — Recycle"* banner, until someone clicked. On a production portal that inversion is an
outage in waiting: after this morning's Store update, the store page served a mixture of old and
new code until each hub was recycled by hand.

Deployments can now opt into **convergence**: with `Modules:AutoRecycleOnStaleBuild: true`, an
instance whose type published a genuinely different build recycles itself — once, only after the
publish burst has settled, and never for node writes that carry the same assembly. The banner
remains the default everywhere else (authoring and local meshes keep the choice per page), and the
guards that once forced the banner-only design — one recycle per binding, the settle window, the
assembly-path gate — bound the opted-in behaviour too.

Production portals should set the flag in their configuration; nothing changes until they do.
