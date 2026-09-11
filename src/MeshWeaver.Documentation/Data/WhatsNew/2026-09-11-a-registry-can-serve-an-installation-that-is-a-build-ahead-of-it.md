---
Name: A registry can now serve an installation that is a build ahead of it
Category: Fix
Description: An installation running a newer platform build than the registry it installs from used to be refused every prebuilt bundle and had to compile all of its plugin content on every start. It is now served the content baked for its own build, whenever the registry holds it.
Icon: Checkmark
Order: -20260911
---

# A registry can now serve an installation that is a build ahead of it

Plugin content arrives at an installation as ready-compiled bundles, so a fresh start does not have
to compile everything itself. Those bundles are only ever used with the exact platform build they
were compiled against — that has not changed, and it is what keeps a page from loading code that
does not match the platform underneath it.

What had gone wrong was one step earlier. When an installation asked its registry what it could be
given, the registry answered with the build **it** happened to be running, rather than the build the
installation asked about. Because the two portals in a fleet roll at slightly different times, that
is the normal state for part of every day — and during it every bundle was refused in one go, before
any of them was even requested. The content still appeared, because the installation compiled it
instead; the cost was a slower start and, on a multi-replica installation, pages that could render
empty until the replica that served them had compiled the type for itself.

The registry now answers for the build the caller asked about, and serves that build's content from
the publication it already holds for it. When it holds none, it answers with its own build exactly
as before — so an installation whose build has genuinely not been baked yet is told so, and compiles,
rather than being offered content that is not there.

Nothing about which bundles may be used has been loosened: an installation still uses only bundles
compiled for its own platform build, and still checks that for the bundle as a whole and again for
every assembly in it.
