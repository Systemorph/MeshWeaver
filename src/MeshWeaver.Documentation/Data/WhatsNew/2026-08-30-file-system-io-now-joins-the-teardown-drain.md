---
Name: File-system I/O now joins the teardown drain
Category: Fix
Description: File-system-backed spaces run their reads and writes on the mesh's tracked I/O pool, so shutdown can cancel and join them — closing the untracked-I/O straggler source behind the FutuRe teardown crash family (#613).
Icon: Sparkle
Order: -20260830
---

# File-system I/O now joins the teardown drain

Spaces backed by a file-system data source — the FutuRe sample is the one such space — were
quietly running every file read and write on an untracked thread-pool bridge instead of the mesh's
own file-system I/O pool. The construction paths those adapters take simply dropped the pool
registry, and the fallback pool they landed on keeps no ledger at all: to the shutdown sequence,
that I/O did not exist. Teardown would report a clean drain while a file read was still in flight,
and such a straggler entering the framework after the mesh's resources were already released is the
teardown crash family tracked as issue #613 (`MeshWeaver.FutuRe.Test exit=139`, with no failing
test in the results).

Every file-system adapter now requires the mesh's pool registry at construction — the compiler
flags any code path that would drop it — and the configuration-driven factory fails with a clear,
named error if a host lacks the registration instead of silently falling back to the untracked
pool. File I/O on these spaces is therefore visible to shutdown like every other pooled operation:
it gets cancelled, joined, and counted before the mesh lets go of its resources.

This closes the untracked-I/O straggler source feeding that crash family; the remaining half of
issue #613 — releasing compiled node assemblies that are still referenced at teardown — stays open
and is tracked there.
