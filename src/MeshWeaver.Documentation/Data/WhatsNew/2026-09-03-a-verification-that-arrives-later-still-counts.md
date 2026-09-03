---
Name: A verification that arrives later still counts
Category: Fix
Description: Your installation checks whether a new platform build can actually run the packages you have installed. It was only ever reading that answer once, at start-up — so an answer produced afterwards was ignored and the update went ahead anyway.
Icon: ShieldCheckmark
Order: -20260903
---

# A verification that arrives later still counts

Before your installation updates itself to a new platform build, something has to answer a question
the version number cannot: **can that build still run the packages you actually have installed?**
A platform change can be perfectly good and still be unable to serve a package built against the
previous one, and the failure lands at start-up — a portal that comes back up broken rather than
one that never moved.

That answer is produced elsewhere and delivered to your installation, where it is recorded and
shown on the **Updates** settings tab. Your installation then refuses any build the answer says
would break a package you run, and says which package.

**The answer almost always arrives after your portal has already been running for a while** — it is
produced when a new build is published, which is days or weeks after your pod last started. And
that was exactly the case being missed: the update watcher read the record once, when it started,
and then went on deciding from that first reading for the entire life of the pod. An answer landing
afterwards changed the stored record, was visible on the Updates tab, and reached the decision
never. In practice that meant the refusal almost never happened — the check existed, was recorded,
was displayed, and did not stop anything.

**The watcher now decides against the record as it stands at that moment.** A verification that
lands while your portal is running is honoured on the next check, the hold appears on the Updates
tab naming the package that would break, and the update waits for a build that can serve you.

Nothing else changed about how updates are paced: this reads the same record more freshly, it does
not check more often, and a clean verification still lets the update straight through. The
same freshness applies to everything else the record carries — a hold cleared by hand, a policy
detail edited on the tab — none of which used to reach a running watcher either.
