---
Name: What you bought follows your account, not the server
Category: Fix
Description: A package you are entitled to is no longer refused by an instance simply because nothing has installed it there yet.
Icon: Sparkle
Order: -20260822
---

# What you bought follows your account, not the server

When one MeshWeaver installation asks another for a package's prebuilt code, the serving side has to
answer a question first: *is this installation entitled to this package?*

It used to answer that question by looking at its own records of what **it** had installed. That
works right up until the package it is being asked for is one it never installed itself — a package
provisioned straight from its repository, say, or one on a brand-new installation where nothing has
been set up yet. In that case there was nothing to look at, and "I cannot tell" came out as **"you
are not entitled to it"**.

That is the wrong answer to give, and it is wrong in the most expensive direction: entitlement is a
fact about the **account**, not about which server happens to be answering. Something already paid
for should not read as unpaid because a particular machine has not caught up.

## What changed

The question now goes to the registry, which is where entitlement actually lives. The serving
instance's own records are still used — they are quick, and they work offline — but as a **cache**:
if they have nothing to say, the question is passed upstream instead of being answered with a no.

There are now three possible answers rather than two:

- **Entitled** — the bytes are served.
- **Not entitled** — nothing is served, exactly as before. Holding a licence to one source still
  does not confer a paid package sitting beside it.
- **Unknown** — the registry could not be reached and nothing here has ever seen this package
  before. Nothing is served, because there is no answer to serve from; but it is recorded as
  *unknown*, and never claimed as a refusal.

## If the registry cannot be reached

Anything whose entitlement was seen here before keeps working. That is deliberate: a registry being
briefly unreachable is not evidence that somebody's purchase has evaporated.

The degraded state is **visible** rather than something you have to deduce. A new
`entitlement_anchor` health check reports **Degraded** while answers are coming from cached
observations, and names how many questions could not be answered at all. It never reports Unhealthy
— continuing to serve what was already known good is the right behaviour, and taking the instance
out of rotation over it would be worse than the degradation.

An installation with no registry configured at all now says so plainly, instead of behaving as
though it were an authority that had checked and found nothing.

## What did not change

Nothing about what a caller can *see*. A refusal is still byte-for-byte identical to "there is no
such package", the index still lists only what the caller may have, and the reason is still written
to the log where only an operator can read it — an installation cannot use these routes to discover
what else a registry carries.
