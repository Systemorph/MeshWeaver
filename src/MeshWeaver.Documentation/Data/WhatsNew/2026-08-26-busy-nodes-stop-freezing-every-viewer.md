---
Name: A busy node stops freezing every viewer
Category: Fix
Description: When many writers edited the same node at once, everyone watching it went blank for seconds and then caught up in a rush — and the busier it got, the worse it got. The owner had already applied every edit; it was the delivery that was doing avoidable work, once per waiting write, on every single update.
Icon: TopSpeed
Order: -20260826
---

# A busy node stops freezing every viewer

When a lot of writes landed on **one** node at the same time — an agent streaming a reply while its
tools write alongside it, an import or sync pushing a batch, several people in one thread — every
view of that node could **stop updating for seconds at a time**, then take every missed update in
one burst.

It looked like the writes were slow, or stuck. They were not. The owner of the node had already
applied all of them, in order, correctly. Nothing was ever lost. What lagged was the *delivery* of
those changes to everyone watching.

## What was happening

Every cross-hub write waits for proof that its own change has been committed before it reports
success. To find that proof it inspects each update the node publishes and asks one question: *is
this the state that contains my write?* — a comparison of two small values, an identity and a
version number.

To read those two values it was rebuilding the **entire node as a document**, every time.

On its own that is merely wasteful. What made it bite is who pays it and when. Every write still
waiting for its proof asks that question about every update that goes past — so a hundred writes in
flight meant a hundred full rebuilds of the node before the next update could be delivered. And the
node they were rebuilding was itself growing with every write.

That is a loop that feeds itself. The more the delivery fell behind, the more writes were still
waiting; the more writes were waiting, the more work each further update had to do before it could
go out; and so on. It did not degrade gently — it held steady and then hit a wall.

Counted on a burst of 288 writes to one node: delivering those 288 changes rebuilt the whole node
**9 455 times**. The owner had finished applying every one of them in **1.5 seconds**, while
everyone watching sat through a **2.7-second gap with no updates at all** before the rest arrived
in a rush.

## What changed

The check now reads those two values **directly off the node**, using the same names the document
would have used, and never builds the document. The answer is identical; the cost no longer depends
on how big the node is or on how many writes are waiting.

The same burst now rebuilds the node **zero** times. Delivery to every watcher completes in
**2.5 seconds instead of 5.4**, allocating a third less memory along the way — and a watcher that
had previously been left stranded three-quarters of the way through now finishes with everyone
else.

## What you will notice

A busy node keeps moving. Live views of a thread, a document or a folder under heavy concurrent
editing update steadily instead of freezing and lurching, and the effect grows with how busy things
are — the case that used to be worst improves most.

It also removes a knock-on: a write whose confirmation arrives too late is reported as a conflict
and re-attempted against what the writer can see. While delivery was stalled, writers could not see
anything newer, so those re-attempts had nothing fresh to rebase on. Keeping delivery current keeps
that recovery path working the way it was designed to.
