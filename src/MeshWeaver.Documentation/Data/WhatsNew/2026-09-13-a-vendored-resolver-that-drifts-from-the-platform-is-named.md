---
Name: A vendored resolver that drifts from the platform's is named
Category: Feature
Description: Every node repository carries a copy of the script that decides which sealed platform set it builds against; the validation lane now compares that copy with the platform's canonical at the code level on every run — advisory for one day, red after — so a stale or forked copy is a named finding instead of a silently different answer.
Icon: GitCompare
Order: -20260913
---

# A vendored resolver that drifts from the platform's is named

Since the fleet stopped pinning platform builds, each node repository resolves the newest sealed
set at run time with `scripts/resolve-platform.py` — a copy of the platform's script. Seven copies
existed on 2026-09-13 at six distinct sizes, one of them an older vintage that answers differently
on a re-run, one a fork with its own pull-request policy, and nothing compared any of them to the
original.

The validation lane every repository calls now fetches the platform's canonical at the lane's
scripts ref and compares the repository's copy with it **at the code level** — docstrings and
comments are not drift — naming the functions that differ. For one day after landing the finding is
a warning; from `2026-09-15T00:00Z` it fails the run. A repository that carries no copy passes: the
lanes resolve the platform themselves, and that is where this ends. The incident freeze
(`MW_PLATFORM_REF`) is not affected — the guard compares files and never runs the resolver.
