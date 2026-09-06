---
Name: An unchecked release event now fails the publish
Category: Fix
Description: A publishing lane that signs its delivery and is told the receiver never looked at the signature now fails, instead of printing a warning nobody reads. And the answer that carries no verdict at all — a receiver that has not rolled yet — is no longer reported as a missing configuration key, which sent operators to fix something that was not broken.
Icon: ShieldKeyhole
Order: -20260906
---

# An unchecked release event now fails the publish

When a build is promoted, the pipeline signs a small record — *this platform build exists, here are
its images* — and posts it to the receiving portal's webhook inbox. The portal answers whether it
actually **checked** that signature, because a `200` on its own only says the bytes arrived.

Two things were wrong with how the pipeline read that answer.

## It could not tell "did not check" from "cannot say"

The step tested for the single word `verified` and reported **everything else** as *"the instance
declares no `SecretConfigKey` for this target"*. That is one specific diagnosis with one specific
fix — and it was printed even when the portal had said nothing of the kind.

Measured on 2026-09-06, on two independent lanes of the same delivery run: the receiving portal
answered `200` with an **empty body**, because it is still running a build from before the verdict
existed. Both lanes told the reader to provision a configuration key on a portal that could not have
read one. The absence of an answer was being printed as an answer.

There are three states, and they now read as three:

- **`verified`** — the portal checked the signature. The step passes, quietly.
- **`not-required`** — the portal ran the check and declares no secret for this target, so the
  signature it was sent was never looked at. **The step now fails**, and names the one key that
  fixes it.
- **no verdict at all** — the portal cannot answer, because it predates the verdict. The step says
  so, and says that this is a rollout gap rather than a missing key.

## The failure is scoped so it cannot cry wolf

Accepting a delivery without checking its signature is perfectly legitimate — that is how
integrations whose signing scheme the portal does not speak have always worked, and nothing about
them changes.

What makes it a fault here is that **the sender signed**. Both publishing lanes require a shared
secret and always send a signature, so being told "we never looked" means the secret is doing
nothing and a drifted one would be as invisible as it was before any of this existed. There is no
switch and no "expect verification" setting to get wrong: the expectation is the act of signing, and
a lane cannot stop expecting without dropping its secret — at which point it fails earlier, saying
so.

The same scoping is why an absent verdict does not fail. It is not a portal declining to check; it
is a portal that has not been deployed yet, which no amount of configuration would fix and which
would otherwise turn every promoted build red until an unrelated deployment happened. The escalation
arms itself instead: the first time a receiver answers a verdict at all, the failing branch becomes
live, with nothing for anyone to remember.

## What you may notice

If you operate an instance that receives signed platform records, its publishing lanes will now go
red — with the key to set, on the record to set it on — rather than printing a warning that scrolls
past. Nothing changes for webhook targets you never signed for.
