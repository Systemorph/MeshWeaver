---
Name: A webhook delivery that stops short is answered, not crashed
Category: Fix
Description: When a webhook sender's connection dropped part-way through delivering its body, the portal treated it as an internal failure — a 500 and a stack trace in the logs — rather than as the incomplete request it was, answered with a 400. The sender simply retries. Both webhook endpoints are corrected, and a truncated delivery is no longer confused with an oversized one.
Icon: Bug
Order: -20260919
---

# A webhook delivery that stops short is answered, not crashed

The portal accepts webhooks on two endpoints — one for GitHub, one general-purpose. Both read the
incoming body under a size cap, because both are reachable without authenticating first and neither
can afford to buffer whatever a stranger decides to send.

Sometimes a delivery does not finish. The sender times out, a probe is abandoned, a connection is
reset. The request announced a body of a certain size and then stopped short of it. This is an
ordinary network condition and it happens to every public endpoint.

## What was happening

The portal treated it as an unhandled failure. The read raised "Unexpected end of request content",
nothing caught it, and it travelled all the way out to the generic error handler — producing an
internal-error response and an error-level entry with a stack trace, six times across nineteen days.

Nothing was wrong with the portal. There was simply less body than the sender had promised, and the
correct answer to that is to say so.

## Two outcomes that must not be confused

There is a second way a body read ends without a result: the body is *too large*, past the cap, and
is refused. Those two look similar in code and mean opposite things — one is the sender's payload
being too big, the other is the sender vanishing mid-sentence — and the statuses that belong to them
are different.

The tempting fix folds them together, which would have answered "your payload is too large" to a
caller whose connection dropped, and recorded a cap breach for a body well under the cap. They are
kept apart instead: a truncated delivery is answered **400**, an oversized one still **413**, and
each is logged as itself.

## Both endpoints

The error was only ever reported against the GitHub endpoint, but both endpoints read bodies the
same way through the same shared reader, so both were exposed and both are fixed. A test now pins
the distinction directly on the reader, so a future change cannot quietly collapse one outcome into
the other.
