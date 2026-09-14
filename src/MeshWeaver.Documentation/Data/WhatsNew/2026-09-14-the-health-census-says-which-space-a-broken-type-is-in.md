---
Name: The health census says which space a broken type is in, instead of only how many
Category: Fix
Description: A portal's public health page could say that exactly one of its dynamic types is permanently broken, and then decline to say whose it was — a number nobody could act on, which reads as if nothing is wrong. It now names the space each outstanding type belongs to. The type's own name stays unpublished, because that page is readable by anyone.
Icon: Search
Order: -20260914
---

# The health census says which space a broken type is in, instead of only how many

A portal publishes a short census of its own health on a page anyone can read. One line of it
reports the types the portal builds for itself: how many there are, how many are ready, and how many
are in a state that needs attention — including the one state nothing else in the platform will ever
mention again.

That state is a type that was **already broken before this version of the portal shipped**. Deploys
deliberately do not stop for it: one abandoned type must not be able to freeze every future release,
discovered at the worst possible moment. The consequence of that — correct — trade is that the
census line is the only place in the entire system where such a type is recorded at all.

And it recorded a number. `previouslybroken=1`. Exactly one, somewhere, on that portal — and not
which one. Everything else that could have answered is scoped to what the person asking is allowed
to see, and the broken type was in a space they were not; each probe answered *"I can't tell"*, for
its own reason, and none of them answered *"no"*. One report was filed, closed as fixed on that
ambiguity, re-filed unchanged four weeks later, and bulk-closed a third time without anything being
read.

**The census now names the space.** The same line continues:

> Non-baked types by partition: previouslybroken in BinaryClickerV2/…

That is enough to route the finding to whoever owns that space, which is all any of those three
reports ever needed. It applies to every outstanding state, not just the broken one — a portal
reporting fifty-eight types still to build now says which spaces they are in, where before nobody
could enumerate them.

**The type's own name is deliberately not published.** This page needs no login, so it names the
space and stops there. A space name is already visible to anyone who can list them; a node's title
is not, and an instrument that works by disclosing other people's titles is a disclosure in an
instrument's clothing. Whether an authenticated view should say more is a separate decision for
whoever owns that page — the information now reaches it either way.
