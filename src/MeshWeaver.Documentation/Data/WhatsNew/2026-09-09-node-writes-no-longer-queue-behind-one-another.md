---
Name: Node writes no longer queue behind one another
Category: Fix
Description: Every create, delete, move and copy in the mesh runs through one execution hub, and that hub processes one thing at a time. The caller's own follow-up work was running there too — so while one create sat waiting inside a validator, every unrelated node write in the mesh waited with it. The follow-up work now leaves that hub, and the writes run in parallel.
Icon: Flash
Order: -20260909
---

A hub handles one message at a time. That is the point of it — it is what makes a node's state safe
to change without locks.

When you ask a hub for something and then do more work with the answer, that follow-up work was
running **on the hub that answered**, inside the same turn. The hub could not move on to its next
message until your follow-up finished.

For most requests that is invisible. For node writes it was not: **every create, delete, move and
copy in the mesh goes through one execution hub**. A create returns its acknowledgement in about a
millisecond and detaches — but the rest of the work (a partition bootstrap, the validators, the
save, the change feed) hung off the answer to a nested request, and that answer came back to the
same hub. So the whole tail of one create ran inside that hub's turn.

With a single create waiting inside a validator, a second and entirely unrelated create sat
unprocessed for the full budget. Every node write in the mesh was serialised behind one.

## What changed

The follow-up work for node writes now leaves that hub as soon as the answer arrives. The hub takes
its next message immediately, so writes that have nothing to do with each other proceed at the same
time.

## Why only node writes

Because doing it for everything is not safe, and that was measured rather than assumed. A hub's
single-file processing is not only about ordering — it is also where an error is caught, where
teardown is fenced, and where the caller's identity is still in scope. Moving *every* follow-up off
it crashed the test host repeatedly and broke several guarantees nobody had written down.

So the behaviour is declared by the requests that need it — the node write operations — and every
other request keeps exactly the semantics it had. A follow-up that has left the hub is fenced
separately: if it throws, the error is reported and the caller's sequence ends, rather than
surfacing unhandled on a background thread and taking the process down.
