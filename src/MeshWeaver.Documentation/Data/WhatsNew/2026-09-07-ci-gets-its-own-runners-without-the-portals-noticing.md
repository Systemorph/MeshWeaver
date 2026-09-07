---
Name: CI gets its own runners, without the portals noticing
Category: Feature
Description: Actions Runner Controller now runs beside the portals on the shared cluster, behind three independent brakes and a priority class that makes the scheduler drop a build before it drops a portal.
Icon: Server
Order: -20260907
---

# CI gets its own runners, without the portals noticing

The fleet's builds queue behind an org-wide ceiling of 60 concurrent hosted jobs. One plugins pull
request is 105 jobs, so a few open PRs saturate the org and everything else waits — measured on the
day this landed: 61 jobs running, 18 runs queued behind them, and two thirds of a monthly budget
spent on day seven.

The cluster that runs the portals had the capacity sitting idle. It now runs the current Actions
Runner Controller in its own namespace, and the interesting part is not that it works — it is what
stops it from ever mattering to a portal.

**Runners rank below production, by construction.** Every portal pod runs at the default priority;
runners run at a negative one and can never preempt anything. So when a portal needs to scale out or
roll, the scheduler removes a *build*, not a replica — and the build simply runs again.

**Three brakes, not one.** A cap on the scale set is a setting, and settings move. Underneath it a
`ResourceQuota` enforces the ceiling in the API server, where no configuration mistake can exceed
it, and a `LimitRange` makes an unbounded pod — the one shape that could crowd a portal regardless
of priority — unschedulable.

**The cap came from a measurement, not a guess.** Sizing this from live CPU usage would have
suggested a limit five times too large: the portals *reserve* far more than they use, and the cloud
portal was sitting one nudge below the threshold that adds two more replicas. The capacity that had
to stay free for it was reserved first; the runner cap is what was left over.

The controller's permissions stop at its own namespace, so it cannot see the portal namespaces at
all, and removing the whole thing is two commands that leave nothing behind.
