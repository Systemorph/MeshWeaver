---
Name: A deployment tells the truth about its database
Category: Fix
Description: A deployment that uses an external database no longer ships an empty password and a made-up connection string for a database it does not have — and the record of a finished database migration now survives long enough for the deploy that started it to read the outcome.
Icon: Database
Order: -20260907
---

# A deployment tells the truth about its database

Two things a deployment says about its own database were misleading, in the same direction: they
read as answered when they were not.

## An unused credential is now absent instead of empty

A deployment can run its database inside the cluster, or point at an external one. Every cloud
deployment uses an external database — and yet each one still shipped the *in-cluster* pair: a
password with nothing in it, and a connection string assembled from that empty password, naming a
database server that deployment does not run.

The empty password was the harmless half. The connection string was not: because it was **built**
from the missing password rather than copied from anywhere, it came out looking entirely
reasonable — the right shape, the right length, a plausible host — while pointing at nothing.
Anything checking whether the deployment was configured saw a value and moved on. Checking that the
value was not *empty* did not help either, because it was not empty; it was invented.

Both are now shipped only where there is an in-cluster database to use them, or where somebody has
deliberately supplied one. Nothing read them, so nothing changes about how a deployment runs — what
changes is that the next person auditing it is told "not configured" instead of being shown a
convincing wrong answer.

**Absent beats empty, and empty beats invented.** A credential that is missing fails at startup
with something you can act on; a credential that is empty fails later, at the moment of connecting,
wearing the words *"authentication failed"* — which sends you looking for a wrong password instead
of a missing one.

## A finished migration leaves a record you can still read

Updating a deployment runs its database migration as a one-off task, and that task is the only
evidence the migration happened. It used to be cleaned up ten minutes after it finished — sooner
than the deploy that started it finishes watching, and far sooner than someone checking back later.

A migration on a large deployment can legitimately run for hours; one recent run took nearly five,
working steadily through every tenant's data. The deploy watching it gives up long before that and
says so. When somebody then goes back to check the outcome, the task must still be there — and
"cleaned up" and "never existed" look identical, as do "finished" and "failed".

The record is now kept for a day. That is what makes a later check able to answer the question at
all, rather than quietly assuming the best.
