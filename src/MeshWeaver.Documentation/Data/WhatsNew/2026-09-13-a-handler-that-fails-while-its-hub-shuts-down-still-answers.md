---
Name: A handler that fails while its hub shuts down still answers
Category: Fix
Description: A request whose handler fails on a hub that is already tearing down now receives a delivery failure at once, instead of waiting out its whole timeout — including during a full portal shutdown, where the answer is handed to the waiting hub directly.
Icon: ArrowReply
Order: -20260913
---

# A handler that fails while its hub shuts down still answers

A request could reach a hub that had already begun shutting down, run its handler there, and
fail — and the caller would hear nothing. The failure was classified correctly and then dropped on
the way out, because the hub's own reporter refused to post once its shutdown had passed a certain
phase. The caller then waited for its full request timeout, and a caller that retries kept asking,
which is the climbing retry wave seen on loaded gate hosts.

During a full shutdown the loss was worse: every hub's parent is already past the phase in which
it forwards answers, so a failure meant for a sibling that was still draining its own callbacks
could never reach it. That sibling waited for its whole drain budget, re-armed it because the
responder was "about to answer", and only then gave up — so the shutdown paced itself on budgets
for answers that existed the whole time.

Both are closed. The reporter now hands every failure to the same seam that forwards ordinary
replies through a live parent, and a delivery failure that no parent can carry is handed straight
to the waiting hub inside the same process. A handler failure caused by the hub's own service
scope having closed is now reported as a transient "shutting down" refusal rather than as an
application error, so callers retry against the fresh activation instead of giving up. The request
trail printed in disposal diagnostics now names the inner exception and every carrier that was
tried, so the next such report says which seam lost the answer instead of "find why".
