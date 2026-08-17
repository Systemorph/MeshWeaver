---
Name: Rebuilds scope to what a type actually uses
Category: Feature
Description: Every compiled type now records exactly which assemblies and modules it binds — updating a module rebuilds only the types that use it, and identical content shares builds across deployments regardless of what else each has installed.
Icon: Sparkle
Order: -20260817
---

# Rebuilds scope to what a type actually uses

Until now, installing or updating any module invalidated every compiled type on the deployment —
the platform only knew "the installed set changed", not who used what. And two deployments with
different module line-ups could never share a build of identical content.

Every compile now stamps a dependency record read off the produced assembly itself: exactly which
platform assemblies and modules those bytes bind, each with the identity it was built against.
Everything that decides whether a build is still valid — the startup warm-up, the bake probe, and
the prebuilt-bundle adoption gate — checks that record against the running environment:

- updating a module rebuilds **only its dependents**; everything else keeps its build;
- a type that uses no module at all is valid on **any** deployment, whatever else is installed;
- prebuilt bundles carry the record, so adoption refuses bytes whose module bindings don't match
  this environment — before they can misbehave, not after.
