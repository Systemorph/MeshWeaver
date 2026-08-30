---
Name: An upsert always answers its caller
Category: Fix
Description: When the read that decides between create and update finished without producing either a node or an error, the upsert handler posted no reply at all — while having already reported the request handled. The caller then waited out its whole budget, and the node's hub could not shut down.
Icon: ArrowReplyAll
Order: -20260830
---

# An upsert always answers its caller

Creating-or-updating a node starts by reading whether the node is already there. That read has
three possible endings, not two: it produces a node, it fails, or it finishes without doing either.

The handler was written for the first two. It answered the caller on a value and on an error, and
said **nothing** on the third — while having already reported the request as handled. The caller
then sat waiting for a reply that could never come, until its whole budget ran out.

It is worse than a slow request, because the hub that owns the node cannot finish shutting down
while a delivery is still outstanding. Measured in CI: a per-node hub holding an entire mesh
teardown open for 19 seconds, with the trail reading

> handler entered → handler exited, marked processed — *and no reply, completion or fault recorded
> since*.

The third ending now has its own answer: a refusal that says the read produced no result, so the
upsert could not decide between creating and updating.

## Why it is a refusal and not a create

The tempting repair is to treat "produced nothing" as "the node is not there" and create it. That is
wrong, and quietly so. An empty read means the read gave **no answer** — which is not the same as
answering "absent". Turning one into the other writes data on the strength of missing information.

So the caller is told plainly that the decision could not be made, and can retry. A wrong answer
delivered promptly would have been worse than the hang.
