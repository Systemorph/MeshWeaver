---
Name: Pod hubs wait for the cluster before announcing themselves
Category: Fix
Description: A portal starting several hubs at once could try to announce them before its cluster was ready, producing a burst of routing errors. The announcements now begin only after the portal is an active cluster member.
Icon: Checkmark
Order: -20260911
---

# Pod hubs wait for the cluster before announcing themselves

Some of the portal's central message hubs are created very early during startup. They used to
announce themselves to the cluster immediately, even though the starting portal had not yet become
an active cluster member. During a rolling update that could leave no active portal able to accept
the announcement, producing a burst of routing errors and a short interval in which those hubs
could not be reached from another portal.

Those announcements now wait for the same lifecycle milestone as the portal's other cluster
routing setup. Once the portal is active, both routing paths start normally. A hub removed before
that point no longer sends a redundant withdrawal for an announcement it never made.
