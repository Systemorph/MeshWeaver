---
Name: A silo checks its Orleans database before starting
Category: Fix
Description: A portal configured for AdoNet clustering now refuses to start — naming exactly what to provision — when the orleans database was never set up, instead of crash-looping on an exception that named nothing.
Icon: DatabaseWarning
Order: -20260817
---

# A silo checks its Orleans database before starting

A portal that runs `Features:Orleans:Clustering=AdoNet` needs two databases: the mesh database
the migration versions, and a separate `orleans` database holding cluster membership and the
grain storage behind `PubSubStore`. Until now the portal only ever verified the first one.

It did check that it had been *told* where the Orleans database is — startup throws when the
connection string is missing. But being told where a database is says nothing about whether
anything ever created it, and those are provisioned by two different containers. When the
connection string was present on the portal and absent from the **migration**, the migration's
Orleans phase logged "skipping" at Information and created nothing; the portal then started, and
the first thing the AdoNet provider did was load its query texts with `.Single()` and throw
`Sequence contains no elements` into a crash loop.

That message names no table, no key, no connection string and no container — it is the least
actionable possible rendering of "the database was never provisioned", and a production roll-out
was reverted on it.

Now both ends of that handover say what they mean:

- The **migration** reads back the `OrleansQuery` rows it was supposed to create and reports them
  counted — `9 membership + 4 grain-storage query keys present` — instead of reporting a generic
  completion. If the rows are missing it fails red rather than claiming success, which also closes
  a gap where a creation script that died part-way left its marker table behind and every later
  run skipped it as "already present", forever.
- The **portal** independently re-checks the same rows against the database its silo will actually
  use, and refuses to start if any are missing — naming which keys are absent and which container
  to provision.

Two checks of one contract, one where it is produced and one where it is consumed. A deployment
whose two halves disagree about whether Orleans is in play now fails loudly at startup, pointing at
the fix, rather than looking healthy until the silo tries to use what is not there.

The check only runs where it applies: it is registered solely when the silo genuinely uses AdoNet
clustering, and it asks for exactly the keys that deployment's configuration causes it to read — so
a portal on Azure Tables or Localhost clustering, or one with an in-memory pub-sub store, is
unaffected. Like the existing database-version gate, it fails closed on anything the database says
and stays silent when a rollout simply replaced the pod mid-startup.
