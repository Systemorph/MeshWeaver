---
Name: Pulling platform images no longer needs a registry credential of your own
Category: Feature
Description: Your portal can now serve container images itself, proxying them from the upstream registry with one credential it holds centrally. Anything that already has a portal token — CI above all — can pull without a second set of registry credentials copied into it.
Icon: Checkmark
Order: -20260905
---

# Pulling platform images no longer needs a registry credential of your own

Plugins already reach you this way. Your portal holds one credential for the place plugins come
from, and everything downstream presents a portal token instead — nobody else needs the original.

Container images did not work like that. Every repository that had to pull one — and in a build
pipeline that is all of them — carried its own copy of the registry username and password, *next to*
the portal token it was already holding for plugins. Two credentials, for one job, in every place.

## What changes

Your portal can now serve images itself. It proxies them from the upstream registry using the one
credential it holds, and everything that pulls authenticates the way it already does. The second set
of credentials can be deleted.

Nothing about how images are *published* changes: they are pushed to the upstream registry exactly as
before, so this can be switched on — or off again — without moving anything.

## What it does not do

**Your portal cannot serve the image that starts your portal.** That pull happens before there is a
portal running to answer it, so the image a cluster boots from keeps coming from the upstream
registry directly. This is for everything else: build pipelines, other installations, and anyone who
should be able to read an image without being handed registry credentials to keep.

The mirror is off until an administrator configures it, and it serves only the repositories they
name — an unconfigured or unlisted repository is simply not found.
