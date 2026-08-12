---
Name: Dependency licences are checked on every build
Category: Fix
Description: A dead AGPL package declaration is gone, and CI now fails when a dependency's licence is incompatible with shipping MeshWeaver under Apache-2.0 / MIT.
Icon: Sparkle
Order: -20260811
---

# Dependency licences are checked on every build

MeshWeaver is open source, dual-licensed Apache-2.0 / MIT. That makes two whole
families of dependency unacceptable: copyleft licences (AGPL / GPL / LGPL), which
are viral for a network-served product, and pay-to-use licences, where a
"community" tier turns into a paid one above a revenue threshold.

Nothing checked for either. A licence is added by a single line in the package
list, the compiler is perfectly happy with it, and every test still passes — so a
licensing problem could sit in the tree indefinitely without anyone noticing. One
had: an **AGPL-licensed PDF library was declared in the package list** and had
been for some time. It turned out never to have been resolved into any project,
so nothing shipped with it, but nothing would have told us either way.

An audit of all 432 packages in the dependency graph — direct *and* transitive —
now backs three changes:

- The dead AGPL declaration is **deleted**.
- **CI fails** when any package in the restored graph carries a licence that is
  not on a documented permissive allowlist (MIT, Apache-2.0, BSD, ISC, MS-PL and
  friends). It reads the real licence from each package's own metadata, and it
  covers transitive dependencies, because a copyleft library that arrives
  indirectly binds the product exactly as hard as one we chose ourselves.
- The two packages that do carry non-permissive terms are recorded as **tracked
  exceptions with a written reason and a linked issue**, so they stay visible
  instead of quietly becoming permanent. Each is being removed.

The check has no way to skip itself: it needs no credential, it runs on every
pull request including forks, and if it cannot resolve a licence it fails rather
than assuming the best.
