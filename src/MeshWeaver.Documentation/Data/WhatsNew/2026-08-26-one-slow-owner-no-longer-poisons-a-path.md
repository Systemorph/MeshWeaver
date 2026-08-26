---
Name: One slow owner no longer poisons a path for the whole pod
Category: Fix
Description: A single unanswered subscription to a busy node left that node unwritable for the rest of the pod's life — every later write failed instantly while reporting a 30-second wait that never happened. On boot this cost each replica one of its default packages, with the installer's own retry unable to help. A connection that fails is now dropped instead of being remembered, so the next attempt starts fresh.
Icon: Sparkle
Order: -20260826
---

# One slow owner no longer poisons a path for the whole pod

Reading or writing a node that another part of the mesh owns opens a live connection to that owner.
Those connections are shared and kept, because opening one is not free — the second reader of a node
joins the first reader's connection rather than making its own.

A connection that **failed** was kept too. If the owner was busy and did not answer in time, the
connection was left in place holding nothing but that failure, and everything that asked for the
node afterwards was handed it. Each of those callers got the original error back instantly — not
after waiting, but immediately, because there was nothing left to wait for. Only restarting the pod
cleared it.

The symptom was the opposite of a hang, and read as nonsense in the logs: a thirty-second timeout
reported after a fraction of a millisecond.

## What you will notice

Most visibly, on startup. Every pod re-asserts the platform's default packages when it boots, and
when several replicas boot together some node's owner is briefly too busy to answer. That one
timeout used to be terminal: the package was stepped over, and the installer's own
fall-back-to-a-full-install repair re-used the same dead connection and failed the same way in under
a millisecond. Each replica came up missing exactly one of its ~34 baseline packages — a different
one per pod — and only the next restart tried again.

More generally, any node that had one slow moment stayed unwritable on that pod until it restarted:
an edit that silently would not save, a view that never filled in, a background job that could not
record its progress.

## What changed

A connection that has failed is now recognised as spent and dropped rather than remembered, so the
next caller opens a fresh one. This is not a retry — nothing re-attempts on a timer, and nothing
loops. It simply stops handing out something that is known to be broken, which is the same rule the
rest of the platform's shared caches already follow: a successful result is worth keeping, a failure
is not.

Failures also now carry the real cause instead of a fixed sentence about a thirty-second wait, so a
log line says which step actually gave up.
