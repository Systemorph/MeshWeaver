---
Name: A boot install no longer writes a Space that syncs its own content
Category: Fix
Description: When a plugin's Space keeps itself current from git, a portal restart no longer installs the plugin's newest files over it — a mix of two versions that made types fail to compile and could block a portal from rolling. The boot summary names every package it held and why.
Icon: LockClosed
Order: -20260917
---

# A boot install no longer writes a Space that syncs its own content

A plugin's Space can have **two writers**: the plugin registry, which installs the package's files,
and a **git sync**, which keeps the same Space at the exact commit the portal's running modules were
built from. Each keeps its own record and neither can see the other's.

Every restart, the boot install wrote the registry's newest files into such a Space. Minutes later
the sync re-imported the Space at its own commit and removed, or rewrote, whatever that commit does
not carry. Nothing was corrupted and nothing said anything — but the install record went on claiming
files the Space no longer had, and the next restart wrote them again.

The cost was a Space holding **two versions at once**. A page from one version beside a helper from
the other does not compile:

```text
CS0117 Error: 'IssueLayoutAreas' does not contain a definition for 'Facts'
CS0246 Error: The type or namespace name 'FleetWatchCadence' could not be found
```

On the instance that operates the fleet this happened on 2026-09-17: three types could not compile,
and a portal update could not finish because a new replica would not come up with a broken type on
it.

**What changed.** A restart's install now asks one question before it writes a Space: *can this
portal prove the version it is about to land?*

- **Nothing else writes the Space** — the normal case, and every local or test portal — is
  unchanged.
- **The version is one the portal's own publication names.** Also unchanged: it is the same version
  the sync is holding the Space at, so the two writers agree and either may write.
- **Neither is true** — the install would land whatever a branch happens to point at, into a Space
  somebody else keeps current. It is **held**: nothing is fetched and nothing is written. The
  Space's content keeps arriving through its sync, and installing from the catalog by hand still
  works and is unaffected.

A hold is never silent. It is reported in the boot's install summary with the reason, recorded
beside the other skips, and written to the log once — never as a failure, because no retry can make
a version provable. Nothing keeps trying: the next restart asks again, so a portal that publishes
the version, or a Space that stops syncing, lifts the hold by itself.

The rule and what it cost to learn: [One Partition, One
Bookkeeping](/Doc/Architecture/OnePartitionOneBookkeeping).
