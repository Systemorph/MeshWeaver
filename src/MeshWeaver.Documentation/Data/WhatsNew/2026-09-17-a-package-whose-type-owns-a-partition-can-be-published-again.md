---
Name: A package whose type owns a partition can be published again
Category: Fix
Description: The quality gate that runs every package type's own tests could not create a test instance of a type that owns its partition, so every repository shipping one — a CRM, for example — stayed red and shipped nothing.
Icon: Bug
Order: -20260917
---

# A package whose type owns a partition can be published again

Before a package is published, the platform runs each of its types' own tests. To do that it creates
one throwaway instance of the type and asks that instance to run them.

Some types own a partition of their own — a CRM client is one — and an instance of such a type is the
root of that partition, so it can only be created at the top level. The gate did not know that: it
always put its throwaway instance *inside* the type, which the platform correctly refuses. The whole
run then went red on a type whose code was perfectly fine, and because a red run publishes nothing,
every change merged into such a repository became unshippable — "Update to latest" kept offering the
last build from before.

The gate now asks the same question the platform asks, and places the test instance at the top level
when the type owns a partition. Where a package ships several such types, each one gets its own
throwaway instance instead of all of them competing for the same name.
