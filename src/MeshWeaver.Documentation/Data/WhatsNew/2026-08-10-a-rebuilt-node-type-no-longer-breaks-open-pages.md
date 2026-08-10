---
Name: A rebuilt node type no longer breaks the pages already open
Category: Fix
Description: Pages could start answering "this area failed to render" after their node type was rebuilt or its hub went idle, and stayed broken until the node was recycled by hand.
Icon: ArrowSync
Order: -20260810
---

# A rebuilt node type no longer breaks the pages already open

A page that had been working could suddenly start answering **"⚠️ This area failed
to render"** with a complaint about a type it had been reading happily a minute
earlier. Reloading did not help. Recycling the node did — and then, some time
later, the same page broke again.

The Store was the most visible casualty: on portals in this state `/Store` showed
the failure box instead of the catalog, so the one page that lists everything
installable could not be opened.

## What was happening

Each node type is compiled into its own assembly, and every *page of that type*
shares it. When the node type's own hub went away — an idle timeout, an explicit
recycle, or the restart that follows a rebuild — the framework reclaimed that
assembly, on the assumption that it belonged to the departing hub alone. It did
not. Every page still open on that type was running the very same code.

Reclaiming it did not actually free anything: the runtime only releases a
compiled assembly once nothing references it, and those pages plainly did. What
it *did* do was announce the release, and the caches that listen for that
announcement dutifully forgot the type — including the one every page consults to
look up its own data. From then on the page held code it could still execute and a
directory that no longer admitted the code existed, which is exactly what the
failure box was reporting.

The window was wide open in practice. A deploy rebuilds every node type on the
portal, so a single update could break every page that happened to be open.

## What changed

A page now holds a lease on the compiled code it runs, for as long as it is
running it. Reclaiming that code waits for the last such page to finish — which is
the moment the runtime could have freed it anyway, so nothing is held longer than
before and nothing accumulates. Rebuilds, recycles and idle timeouts all still
reclaim exactly what they used to; they just no longer pull the floor out from
under a page that is standing on it.

Nothing to do on your side. Any page still showing the failure box comes back on
its own after the update.
