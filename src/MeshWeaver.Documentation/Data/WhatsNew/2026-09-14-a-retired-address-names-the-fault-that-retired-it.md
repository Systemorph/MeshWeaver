---
Name: A retired address now names the fault that retired it
Category: Fix
Description: When a part of the portal cannot start because something it depends on is briefly away, it stands down and comes back on the next request. The waiting caller was sometimes told only "your request was never processed" — true, but not the half that says what to expect. It now names the fault, every time.
Icon: ArrowSync
Order: -20260914
---

# A retired address now names the fault that retired it

Parts of the portal start on demand. When one of them cannot start because something it depends on
is briefly away — a database that has not come back yet, a name that does not resolve for two
minutes — it does not mark itself broken. It **stands down**, answers whoever was waiting with "ask
again", and the next request brings up a fresh one against a dependency that has recovered.

The "ask again" was always correct. What it said was not always complete.

## What a caller saw

Two sentences were competing to answer the same waiting request:

- **the one the stand-down wrote** — *"its initialization met a transient infrastructure fault
  (could not connect to the database) and this activation is retired"*;
- **the generic one the shutdown writes** for anything it finds still waiting — *"the message was
  never processed"*.

Both said "retry", so nothing broke. But the two were produced on different threads, roughly a
millisecond apart, and either could get there first. When the shutdown won, the caller was told that
its message had not been processed and nothing more — so a person reading the error saw a recycle
where the honest answer was "the database was away".

Worse, the loss was total rather than partial: once the shutdown had started, the stand-down's
attempt to record its reason had nothing left to record it on, so the cause was not merely late, it
was dropped.

## Why it could not simply be reordered

The obvious fix — say the reason first, shut down second — did not work, and the reason is the
interesting part.

The refusal carries a classification as well as a sentence: *transient* ("ask again") or *terminal*
("this is broken"). That classification used to be worked out at the last moment, by looking at
whether the shutdown had started yet. So saying the reason **first**, before any shutdown existed,
produced the *terminal* classification — precisely the verdict this whole behaviour was built to
avoid.

The two statements were therefore ordered the wrong way round for a real reason, and a comment in
the code said so. What a comment cannot do is make two things happen in order when they happen on
two different threads.

## What changes

The classification is now **stated by whoever stands the address down**, and travels with the
reason, instead of being inferred from how far a shutdown has got. That removes the thing the old
ordering was protecting — so the reason is recorded first, before any shutdown exists that could
answer differently, and the caller learns the cause on every run rather than most of them.

Nothing waits longer, nothing retries, and no time limit moved. A shutdown still lets work it has
accepted finish; it simply no longer gets to overwrite an explanation that was already written.
