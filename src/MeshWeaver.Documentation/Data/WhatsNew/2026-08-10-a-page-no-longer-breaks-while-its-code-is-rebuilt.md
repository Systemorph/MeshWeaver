---
Name: A page no longer breaks while its code is rebuilt underneath
Category: Fix
Description: Opening a page — the Store catalog, for instance — right after its node type was recompiled could fail with "type is unknown", because the running page's type registrations were dropped the moment the new build loaded. They now stay valid until the page actually switches to the new build.
Icon: ArrowSync
Order: -20260810
---

# A page no longer breaks while its code is rebuilt underneath

Dynamic node types are compiled code, and compiled code gets rebuilt — after a
framework update, after an edit, after a release. A rebuild is supposed to be
seamless: the old build keeps serving until the new one is ready, then the page's
hub quietly switches over.

There was a gap in that seamlessness. The moment a new build loaded, the old
build was marked for unloading — which is correct, that is how the old code's
memory is eventually reclaimed. But marking it also *immediately* erased the old
build's type registrations from the running system, even though the hub serving
your page was still executing that very code. The hub itself still knew its
types; the registry it asks no longer did. Anyone opening the Store in that
window got a failed render — "type 'StorePackage' is not registered" — for a
catalog that was perfectly healthy seconds before and seconds after.

The registrations are now demoted instead of erased. While anything is still
running on the old build, its types keep resolving exactly as before, so a page
served by the old code keeps working until its hub switches to the new build.
The demoted registrations hold the old code only weakly, so nothing about the
cleanup is lost: once the last user of the old build is gone, it is reclaimed
just as before — the memory-leak and crash protections that the eviction was
built for remain fully in force.
