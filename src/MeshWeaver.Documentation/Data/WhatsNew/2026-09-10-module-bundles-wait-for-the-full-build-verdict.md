---
Name: Module bundles wait for the full build verdict
Category: Fix
Description: A module bundle is no longer handed to the registry until every validation job in the run that produced it has reported success, so a build that goes red later cannot leave a module already served to installations.
Icon: ShieldCheckmark
Order: -20260910
---

# Module bundles wait for the full build verdict

A module bundle used to be handed to the plugin registry from inside its own build step, right
after that module's own tests. The rest of the run — the other modules' suites, the portal host
tests, the node-type compilation check and the content gate — was still running or had not started.
A failure in any of those changed nothing about the bundle: it was already being served, and every
installation reconciling against the registry could adopt it.

The hand-over now happens in its own step at the end of the run, and only when every required
validation job has reported success. A job that failed, was cancelled, was skipped, or never
reported at all each stops the hand-over and says which one it was — so a build that goes red for
any reason leaves the registry exactly as it was.

The bundles are still built, tested and made available to the rest of the run at the same moment
they always were; only the publication moved. Nothing about how installations receive an already
published module changes.
