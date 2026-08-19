---
Name: Installs stop recompiling what they just adopted
Category: Fix
Description: A package install or deploy that adopts a prebuilt assembly now keeps it, instead of rebuilding the same type moments later.
Icon: Sparkle
Order: -20260818
---

# Installs stop recompiling what they just adopted

Installing a package, pushing content, or booting a fresh deployment can reuse assemblies that were
already compiled by CI, instead of compiling them again on the spot. That reuse was being thrown
away: the moment a type with source files adopted a prebuilt assembly, its record still said "these
sources have not been compiled", so the very next build request rebuilt it anyway — no error, no
warning, just the wait the reuse was supposed to remove.

The record is now completed by the type itself, which is the only place that knows its current
source set, so an adopted build reads as current and the build request that follows an install is
answered instead of re-run. Installs and first-boots that ship prebuilt content are correspondingly
quicker, and the pages that depend on those types are ready sooner.
