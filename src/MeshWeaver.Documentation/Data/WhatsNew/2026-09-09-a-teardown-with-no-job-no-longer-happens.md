---
Name: A teardown with no job no longer happens
Category: Fix
Description: After installing a package the platform tears down and re-activates that package's root, so the new hub binds the package's own configuration. It did that even when the install had written nothing at all — a teardown that could change nothing, which cancelled whatever work was in flight underneath it. It now runs only when the install actually rebuilt something, on the same condition as the recompiles either side of it.
Icon: ArrowSync
Order: -20260909
---

Installing a package ends with the package's root being recycled: the hub is torn down so the one
that comes back binds the package's own configuration instead of the placeholder it was created
with. That is worth doing when the install rebuilt the root's type — and only then.

It ran unconditionally. Measured on a delivery run of 2026-09-08: one package logged
**`0 written, 21 unchanged`** and recycled its root anyway, and another was recycled **twice in one
bake**. Nothing could come of those teardowns — no type had been rebuilt, so the hub that came back
bound exactly what the old one already had.

They were not free. Each teardown cut off the correlated work in flight beneath the root — the
root's own background reconcile — which the shutdown could not answer. It was force-cancelled at the
shutdown's two-second window and reached the caller as *"the hub was disposed before the response
arrived"*.

## What changed

The recycle now runs only when the install wrote something. That is the same condition the two
recompile waves on either side of it already used, and the inconsistency was the bug: the waves knew
an install that changed nothing has nothing to recompile, while the teardown between them did not.

## And the log line stopped over-promising

It used to end *"Work in flight beneath this root is answered by the teardown, not abandoned."* That
was measurably untrue — pending work was force-cancelled at the window, not answered — and a
diagnostic asserting a guarantee the code does not keep is worse than one that says nothing, because
it sends the next reader looking for a different cause. It now says what actually happens: work gets
the shutdown window, and anything still pending at that bound is cancelled and surfaces to whoever
issued it.

## What this does not fix

An install that *did* write still recycles a root whose background pipeline may be issuing requests,
and a hub that keeps accepting new work while it is shutting down is a separate defect. What is gone
is the class where the teardown could accomplish nothing — removed by making the recycle agree with
its neighbours, not by widening any window.
