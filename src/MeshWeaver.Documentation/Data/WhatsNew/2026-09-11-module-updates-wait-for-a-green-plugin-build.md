---
Name: Plugin catalog modules wait for a green plugin build
Category: Fix
Description: A module from the plugin catalog used to be offered to every portal the moment its own build finished, even when the rest of the same build later failed. Once the plugin catalog's build adopts the new publication step, its modules reach the registry only after every validation job of that build has passed. Other module sources are not covered yet.
Icon: ShieldCheckmark
Order: -20260911
---

# Plugin catalog modules wait for a green plugin build

Modules from the plugin catalog, such as the AI engine, Maps or Mail, reach your portal through the
plugin registry. Until now each of them was handed to the registry as soon as its own tests passed,
while the rest of the same build was still running: the other modules' tests, the portal-host
tests, the compile check of every node type and the tests shipped inside the content. When one of
those failed later, the update had already been offered to every portal.

The platform now provides a separate publication step that runs only after every validation job of
the build has passed. The build prepares each module and sets it aside, and if anything failed, was
skipped or was cancelled, the registry is left untouched and your portal keeps the version it has.
The plugin catalog's own build switches to this step in a paired change, and the rule takes effect
for its modules from that point on.

One consequence is visible: while the plugin catalog's build is red, none of its modules publishes
a new version, so an update can arrive later than before. What does arrive has passed the whole
build.

**Not covered yet.** Modules published by other repositories (the social-media modules, for
example) still reach the registry straight after their own tests, and the platform's own sealed
bake of the plugin catalog is a separate publication path. How the step is gated, and what remains
open, is written up under [The Module Publication Gate](/Doc/Architecture/ModulePublicationGate).
