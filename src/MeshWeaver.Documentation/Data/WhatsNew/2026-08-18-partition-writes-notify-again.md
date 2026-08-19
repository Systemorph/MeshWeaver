---
Name: Partition writes notify again
Category: Fix
Description: A trigger guard that asked the wrong question meant only the first schema in a database ever emitted change notifications for node writes — measured at 1 of 33 schemas, and that one is empty by design.
Icon: BellAlert
Order: -20260818
---

# Partition writes notify again

Every write to a node table is supposed to fire a PostgreSQL `NOTIFY` so that anything watching
outside the writing process learns the node changed. On a measured database, **32 of 33 schemas
never did** — and the single schema that did is `public`, which holds no content by design. So in
practice, no node write in that database had ever emitted a notification.

The cause is a guard that asked a question one word too broad:

```sql
IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify')
```

Trigger names in PostgreSQL are unique **per table**, but `pg_trigger` is a **database-wide**
catalog. So the first schema to be created satisfied the condition for the entire database, and
every schema provisioned afterwards quietly skipped installing its own trigger. Nothing failed;
each schema simply decided the work was already done, because somewhere else it was.

Two things kept this hidden for a long time. Satellite tables — the ones holding access grants,
threads, activities and comments — were never affected, because their script always created the
trigger the correct way; 231 of them were installed properly in the same database. And within a
single process, change notifications also travel over an in-memory feed, which kept live views
working for the common case. What was lost was only the notification that had to cross a process
boundary, and it was lost in silence.

This is the same defect that was found and repaired once before for the trigger that records
version history — in one of the very same scripts. That repair fixed one trigger and left its
neighbour on the broken guard. And when a test was written to reject the *pattern* rather than that
one trigger, it immediately found a third instance nobody had noticed: the history trigger was still
being created the broken way in another script, so a newly created schema would have inherited the
old bug all over again.

Both halves are now fixed: new schemas create the trigger per table, the way every other trigger in
the schema scripts already did, and a migration installs it on existing schemas that are missing it.
The migration only touches schemas that actually lack the trigger, so it takes no locks on the ones
that are already correct, and it reports how many were missing rather than simply reporting success.

A test now rejects the *shape* rather than this one instance: any trigger created under a guard that
checks only a name, without also constraining the table, fails the build.
