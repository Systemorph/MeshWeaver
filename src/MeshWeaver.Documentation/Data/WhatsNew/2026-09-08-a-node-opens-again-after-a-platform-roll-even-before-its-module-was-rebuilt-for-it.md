---
Name: A node opens again after a platform roll — even before its module was rebuilt for it
Category: Fix
Description: After a platform update, a page whose module had not yet been rebuilt for the new platform could stay empty for hours — and after a restart it could stay empty until someone recompiled it by hand. A portal now adopts a module built for the same platform line when it can prove the module loads, rebuilds one it cannot prove, keeps its sources in step with what was built, and says on its health page which types it cannot show.
Icon: ArrowSync
Order: -20260908
---

# A node opens again after a platform roll — even before its module was rebuilt for it

Opening a client's page on the staff portal on 2026-09-08 showed an empty frame — the page's
frame rendered, the page's content did not — on both replicas, for hours. Nothing was wrong with
the client's data. The module that declares the page's content type was not loaded on either
replica.

## What was happening

Three things lined up, and each one alone would have been survivable.

**The module's prebuilt bytes were declined.** A portal only ever adopted a prebuilt module built
for exactly the platform build it was running. After a platform update, every module had to be
rebuilt for the new build before the portal would take it — and until then the portal compiled the
module itself from its sources.

**Its sources did not match what had been built.** The portal's copy of the module's sources had
kept three files a repository commit had already deleted, so the bytes built from that commit never
matched the sources the portal held, and the portal kept compiling its own — different — build.

**After a restart, that build was gone.** A build the portal compiles itself lives on the replica
that compiled it. When the replicas were replaced, the record still pointed at that build, nothing
rebuilt it, and every read of the type's content came back untyped. The health page said
`Degraded` and nothing else.

## What it does now

- **Same platform line, proven to load.** A portal on platform 3.1 adopts a module built for any
  3.x platform when the module's declared minimum is satisfied *and* the portal can prove, from the
  module's own metadata, that every platform type it links against is present. A module it cannot
  prove is compiled from source instead. This is the `Modules:VersionStrictness` setting: `Family`
  by default, `Minimum` (no line check) on a development machine, `Exact` for the old rule.
- **Sources follow the build.** When a module's build is sealed for the portal, the portal brings
  its sources for that repository onto the commit it was built from — including files the commit
  removed — so the two cannot drift apart. A person's own uncommitted edit on the portal is still
  kept; only the leftovers of an earlier sync are cleared.
- **No dangling build.** If the portal declines a prebuilt module and its own build of that module is
  no longer on this replica, it rebuilds it immediately rather than leaving the type unreadable.
- **The health page says what it cannot show.** `/health` now names each type whose content this
  replica could not read, with the count, instead of a bare status word.
