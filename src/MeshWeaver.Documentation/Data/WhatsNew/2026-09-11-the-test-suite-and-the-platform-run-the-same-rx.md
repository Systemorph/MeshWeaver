---
Name: The test suite and the shipped platform now run the same Rx
Category: Fix
Description: The reactive library that every stream in the platform is built on was pinned at version 6 for the product and version 7 for the tests that gate it. Both now run version 7, and a guard was added so the pair cannot drift apart again.
Icon: Checkmark
Order: -20260911
---

# The test suite and the shipped platform now run the same Rx

Everything in MeshWeaver is a stream. Every message a hub handles, every layout area that renders,
every read and write to the mesh is built on one library — Reactive Extensions for .NET.

That library was pinned twice, in two places, at two different major versions. The product was built
against **Rx 6**. The test suite that decides whether the product is fit to ship pulled in **Rx 7**,
because the companion testing package it uses requires it. Nothing was red, nothing failed to
restore, and nobody could see it: the two pins sit five lines apart and share no common name, so the
existing check for split package families — which matches on a shared prefix — was structurally
unable to relate them.

A test suite running a different major version of its most load-bearing dependency from the one that
ships is the one gap a suite must never have. Both pins now read the same version, so the platform
is tested on what it runs.

Rx 7 itself is a packaging release rather than a behavioural one: no public API was removed, and
scheduler behaviour, error propagation and subscription disposal are unchanged — which was verified
against the library's own compatibility baseline before the move, not assumed.

The check that missed this can now describe a family by naming its members rather than guessing from
a shared prefix, and it carries a test that fails on exactly the pairing which was live here — a
guard nobody has seen fail is not yet a guard.

The method behind both of this week's major version moves is written up under
[Dependency Major Upgrades](/Doc/Architecture/DependencyMajorUpgrades).
