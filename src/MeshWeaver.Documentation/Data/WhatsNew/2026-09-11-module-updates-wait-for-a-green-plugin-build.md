---
Name: Module updates wait for a green plugin build
Category: Fix
Description: A plugin module update used to be offered to every portal the moment its own build finished, even when the rest of the same plugin build later failed. Module bundles now reach the registry only after every validation job of that build has passed.
Icon: ShieldCheckmark
Order: -20260911
---

# Module updates wait for a green plugin build

Plugin modules such as the AI engine, Maps or Mail reach your portal through the plugin registry.
Until now, each module was handed to the registry as soon as its own tests passed — while the rest
of the same build (the other modules' tests, the portal-host tests, the compile check of every node
type and the tests shipped inside the content) was still running. When one of those failed later,
the update had already been offered to every portal.

Now the build only prepares each module and sets it aside. The hand-over to the registry happens in
a separate step that runs only after every validation job of that build has passed. If anything
failed, was skipped or was cancelled, the registry is left untouched and your portal keeps the
version it has.

One consequence is visible: while the plugin build is red, no new module version is published at
all, so an update can arrive later than before. What arrives is a version whose whole build passed.

How the hand-over is gated, and what it does not cover yet, is written up under
[The Module Publication Gate](/Doc/Architecture/ModulePublicationGate).
