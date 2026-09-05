---
Name: An assertion that has returned is no longer watching
Category: Fix
Description: Tests written with the reactive assertions can now measure whether anything is still subscribed to a stream, immediately after awaiting an assertion on it. The wait releases its own subscription before it hands control back, instead of a moment afterwards — so a test no longer intermittently measures itself.
Icon: Checkmark
Order: -20260905
---

# An assertion that has returned is no longer watching

`await stream.Should().Within(...).Emit()` reads as one thing: *wait for this, then carry on*. The
"carry on" half quietly meant something weaker. The wait handed control back to the test and released
its subscription **a moment later**, on another thread.

Almost always that gap is invisible. It is not invisible to a test whose next question is *"is
anything still subscribed to this?"* — and that question is common, because it is how you check that
a cache entry can be reclaimed, that a claim was released, or that an idle sweep is allowed to run.
Such a test would ask, get the answer "yes, something is", and fail; and because the gap is a handful
of instructions wide, it would only do so on a loaded machine, a few runs in a hundred. One landed in
this repository's own suite: a cached stream entry refused to be released because the only thing
still holding it was the assertion that had just returned.

## What changes

The wait now releases its subscription **before** it settles, so *"the assertion returned"* means
*"the assertion is no longer watching"*. A test may rely on that with no grace period, no polling,
and no retry.

Nothing else about the assertions changes: they still never resume your test on the thread that
signalled, and a fault arriving after a wait has already settled is still reported rather than
dropped.
