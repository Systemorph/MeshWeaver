---
Name: A module published twice at once keeps its newer version
Category: Fix
Description: When two builds of the same module reached two replicas of a portal within a few seconds, the one that finished writing last decided which version the portal ran — so an older build could silently replace a newer one, and the older build's own fallback could be lost. Every landing now keeps its own record and the running version is worked out from all of them, so every replica agrees on the newer version whatever order the uploads finished in.
Icon: ArrowSync
Order: -20260915
---

# A module published twice at once keeps its newer version

A portal runs several replicas that share one module store. When a new build of a module is
published, whichever replica receives the upload puts the bytes in place and records which version
the portal should run from now on.

## What went wrong

That record was **one file per module**, and each replica wrote its decision over it. A replica read
the file when the upload arrived, took a few seconds to put the bytes in place, and then replaced the
file with its own decision. If a second build of the same module reached a **different** replica in
those few seconds, both decided against what they had read before the other one wrote — and the one
that finished last won.

That was the same mistake an earlier fix (the registry never rolls a module back unattended) had
already closed for uploads arriving one after the other. Arriving at the same moment on two replicas,
an older build finishing last could still put the older version back — and even when the newer build
won, the older one lost its place as the fallback and was cleaned up a few minutes later.

## What happens now

Each upload writes a **record of its own** and never overwrites anyone else's. The version the portal
runs, and the one it falls back to, are worked out from all the records present by the same rule as
before — a newer version wins on the publish route, a rebuild at the same version wins, and an
install you chose yourself in the Store still takes effect. Every replica reads the same records, so
every replica agrees, whichever upload finished first.

## What you will notice

Nothing, unless you were hit by it: a module you had just updated no longer comes back at the older
version after two builds of it were published at once. Modules already installed keep running exactly
the version they ran before the update — nothing is rewritten, and a replica still on the previous
portal version during a rolling update reads the store exactly as it always did.
