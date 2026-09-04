---
Name: Module updates reach your portal again
Category: Fix
Description: Plugin modules stopped being published for the fleet — two of them could not be built at all, and the ones that were built each claimed to have been compiled against a different platform, so no portal could tell an update from something it already had. Modules are published again, and every module from one release now states the same platform build.
Icon: Box
Order: -20260904
---

# Module updates reach your portal again

Plugins reach your portal as **modules** — self-contained bundles the portal downloads and loads,
separately from the portal image itself. Every bundle records which build of the platform it was
compiled against, and your portal uses that record to answer one question: *is this bundle actually
newer than what I already have?*

A module's version number alone cannot answer it. The same source rebuilt against a newer platform
republishes under the same version, so without the platform record your portal would see "same
version" and skip an update it needed.

## What was broken

Two things, and they had the same cause: the record was being read from the wrong place — from the
module's own build output, instead of from the platform.

**Two modules could not be published at all.** Maps and the Stripe payments module do not happen to
include the platform component that record is read from, so publishing them stopped with an error.
Because module publication is all-or-nothing, that stopped publication of *every* module. No plugin
update reached any portal on that path.

**The modules that did publish disagreed with each other.** Where the component was present, it had
been rebuilt as part of that module, stamped with that module's own version number — so each bundle
recorded a different platform build, even though they were all built from one platform, in one
release. A record that names a platform build no portal has ever run is a record no portal can match,
so those bundles could not be recognised as updates either.

Neither symptom looked like a problem from the outside. Portal images kept publishing normally
throughout, and the bundles that did get built looked perfectly well-formed.

## What changes

The platform record is now taken from the platform, once per release, and every module built in that
release states the same value. Two checks keep it that way: the packaging tool refuses outright to
read the record from a module's own output, and the release pipeline confirms it has the platform's
copy before any module is built.

For you this means plugin updates flow again, and a portal deciding whether to take one is comparing
against something real.
