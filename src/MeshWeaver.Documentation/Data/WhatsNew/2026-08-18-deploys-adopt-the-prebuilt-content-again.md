---
Name: Deploys adopt prebuilt content again
Category: Fix
Description: CI's compiled content was being published to an address no portal ever read, so the first pod of every deploy recompiled everything — for ten minutes, while pages showed an error card.
Icon: TopSpeed
Order: -20260818
---

# Deploys adopt prebuilt content again

CI compiles the platform's shipped content once, on release, so that a portal starting up can
simply load the result instead of running the compiler itself. That saves about ten minutes on
every pod start — and since 2026-08-17 it had quietly stopped working.

Nothing failed. The compile ran, the bundles were published, the release went out green. They were
just filed under the wrong name: the machine that compiles them and the portal that loads them had
begun disagreeing about which build of the framework they were talking about, so the portal looked
for something under a name that was never written, found nothing, and compiled all 269 content
types itself.

That is the ten-minute window in which the covers of every course on
[memex.meshweaver.cloud](https://memex.meshweaver.cloud) served an error card to anonymous
visitors for two hours on the evening of 2026-08-17. The card outlived the window that caused it;
the window is now gone.

The disagreement came from a genuinely good change made hours earlier — moving the Excel/CSV
import stack out of the portal into its own module. Eight
assemblies left the portal's compiled reference list as a result, and that list is exactly what
each side uses to describe "which framework is this". Two descriptions, two names, one release.

The portal now declares those eight again, purely as a description of the framework it was built
against — the module keeps shipping the actual code, so nothing about the import module changed.

More importantly, release builds now **compare the two names out loud** and fail if they differ,
naming which assemblies caused it. A publication that nothing can read used to look exactly like a
successful one from the outside. It no longer does.
