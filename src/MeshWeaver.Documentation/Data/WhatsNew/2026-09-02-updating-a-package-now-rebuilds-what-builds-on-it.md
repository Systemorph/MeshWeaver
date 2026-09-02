---
Name: Updating a package now rebuilds what builds on it
Category: Fix
Description: Updating a package that other packages build on used to leave those packages running the old code — pages went blank with no error anywhere. The update now rebuilds them too.
Icon: ArrowSync
Order: -20260902
---

# Updating a package now rebuilds what builds on it

Packages can share code. One package publishes source files; another says "build those into me too",
and from then on the second package carries its own copy of that code. That is a real dependency,
and it means the second package is out of date the moment the first one changes.

Until now, updating the first package rebuilt only *itself*. Everything built on top of it kept
running the code it had been built with — and nothing said so. The two halves of the platform then
disagreed about what the shared shapes were, which came out as a page that renders **empty**: no
error banner, no failed install, nothing in the log to search for. The install even reported
success, because as far as it could tell it had rebuilt everything it knew about. It had; the
problem is that a package can only ever know about itself.

This is what took the Store down on 25 August. Every one of the Store's own pieces rebuilt cleanly
and the page still went blank.

**An update now rebuilds the whole set that depends on it**, worked out across the entire mesh
rather than inside the updated package: whichever packages build the changed files into themselves
are rebuilt as well, dependencies first so nothing is rebuilt against something that is still
moving. It applies to every kind of install — a full package, an incremental update, a single code
package, and a plain content package, which previously triggered no rebuild at all even when the
files it shipped were somebody's source code.

Two details worth knowing:

- **Deleting a shared file counts as a change.** A file the package no longer ships makes its
  consumers just as stale as an edited one, and they are rebuilt for it too.
- **Nothing is rebuilt twice.** The update still rebuilds its own pieces first, exactly as before,
  and the wider set skips whatever has already been handled — so an update does not get slower for
  packages that nothing depends on.

There was a second, quieter route to the same blank page, and it is closed in the same change: when
a package's type definition arrived in a slightly different shape than expected, the installer
concluded there was nothing to rebuild *at all* — and issued no rebuild, silently. That check no
longer depends on the shape the content happened to arrive in.
