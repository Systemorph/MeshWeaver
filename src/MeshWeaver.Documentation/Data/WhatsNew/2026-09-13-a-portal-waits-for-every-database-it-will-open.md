---
Name: A portal waits for every database it will open
Category: Fix
Description: The start-up check that waits for the database watched one host, chosen from a different setting than the connections the portal actually opens — so a deployment whose cluster-membership database lived elsewhere passed the check and then died on a name that never resolved.
Icon: DatabaseWarning
Order: -20260913
---

# A portal waits for every database it will open

A portal and its database start at the same time. The database usually wins that race, but not
always, so the portal's pod holds a small gate in front of itself: *wait until Postgres is accepting
connections, then start*. It is one of the few genuinely deterministic things about a deployment.

It was watching the wrong door.

## What went wrong

The gate asked one setting where the database lives. The portal, seconds later, opens its
connections from a **different** setting — a separate, privately held one that carries the actual
credentials. In the common case the two name the same host and nothing is wrong. Nothing ever
checked that they did.

A deployment can legitimately keep cluster membership on its own database server, separate from the
one holding the content. Configure that, and the gate was watching a host the portal never opens
while the host it does open was watched by nothing at all.

What that produced was a boot that got further than it should have and then stopped: the gate
passed, the portal started, and the part that joins the cluster failed on a hostname that did not
resolve. The container exited — correctly; a portal that cannot find its cluster refuses to serve
rather than serving something half-connected.

The expensive part was the reading. **The gate passing said nothing about the connection that
failed**, so the failure looked like a momentary network hiccup rather than like a check that had
never been covering that host. Only the deployment style saved it: the roll brings the new pod up
before retiring the old one, so the previous version kept serving and the cost was a stalled roll
rather than an outage.

## What happens now

The gate is derived from the connections themselves rather than from a setting that merely looks
like them. It waits for **every** distinct database the process will open — the content database
always, and the cluster-membership one whenever that is configured separately — and it names each
one in the log as it waits. "Waiting for the database" and "waiting for the *right* database" are
now the same sentence.

The connection strings themselves are also now written in one place instead of three, so the
migration, the portal and the gate cannot drift into disagreeing about where the database is — a
property the configuration already claimed and did not have.

One deployment path needed a second change to make that true. The script that sets up an external
database used to hand the connection string to the cluster *after* the deployment had been prepared,
by editing the stored value in place. A value that arrives after preparation cannot reach a gate that
was written during it — so on that path the gate would still have watched the wrong door. The string
now goes in with everything else, which also removes a long-standing hazard: because preparing a
deployment replaces stored values wholesale, the next routine update would overwrite the working
connection string and point the portal at a database that is not running until someone re-applied
the edit by hand.

Where a database is configured with a list of interchangeable servers, the gate waits for any one of
them to answer — which is what the portal itself needs — rather than for the first one only, or for
all of them.

Two smaller things follow from it. A configuration naming no host at all now fails when the
deployment is prepared, with a message saying so, rather than rendering a gate that waits for
nothing and passes. And the deployment's own consistency check has a new rule: every database host
the portal will open must be a host the gate waits for — checked against a deployment shape whose
two databases deliberately live on different servers, so a check that had stopped covering anything
would say so instead of staying quiet.
