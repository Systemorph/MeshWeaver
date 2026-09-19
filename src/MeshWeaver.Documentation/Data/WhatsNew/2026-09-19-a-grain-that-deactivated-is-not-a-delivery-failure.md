---
Name: A grain that deactivated is no longer reported as a delivery failure
Category: Fix
Description: When Orleans deactivates an idle grain, a message already in flight to it is forwarded and, once the forwards run out, rejected. The router reported that to the sender as a terminal failure — so every consumer with its own recovery machinery tore it down, for a target that comes back on the next call. It is now classified as a lifecycle transition, like the silo-level shapes beside it.
Icon: Bug
Order: -20260919
---

# A grain that deactivated is no longer reported as a delivery failure

The router's delivery classifier already had a rule, and it is a good one: only conditions that are
**a lifecycle transition by construction** are reported to the sender as transient, and *anything it
does not recognise stays terminal, so a genuine defect is still reported as one*.

Two such conditions were recognised — the grain directory mid-handoff, and the host going away. A
third was not: Orleans deactivating an **idle grain**. A message in flight to it is forwarded, and
when the forwards are exhausted Orleans rejects with

```
… for 2 times after "DeactivateOnIdle was called." to invalid activation. Rejecting now.
```

which is a lifecycle transition by exactly the same argument — the grain-level analogue of the host
going away, and the target re-activates on the next call. Reported as a terminal failure it tore
down every consumer carrying its own recovery machinery, which is the damage already removed for
the silo-level shapes and left standing for this one. Measured on 2026-09-17: 191 occurrences
against a single address.

## The signal is Orleans' own text, and that is a known weakness

There is no typed rejection reason to read — the rejection type carries every kind of refusal, so
accepting it alone would classify genuine refusals as transient, which is the one direction the
classifier says must not happen. The test is therefore the narrowest phrase that means *the
activation is gone*.

If Orleans re-words that message, this quietly returns to the previous answer rather than
misclassifying anything — the safe direction to fail in. The test beside it pins the **production**
string verbatim rather than a paraphrase, and the opposite direction is pinned too: an ordinary
rejection, an unrelated fault and a timeout all stay terminal. A timeout especially — a target that
did not answer across the whole budget is plausibly wedged rather than restarting, and demoting it
would produce a resubscribe storm against a hub that never comes back.
