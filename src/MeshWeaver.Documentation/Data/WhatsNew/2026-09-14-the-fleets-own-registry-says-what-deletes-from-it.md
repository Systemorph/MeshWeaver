---
Name: The fleet's own registry now says what deletes from it
Category: Fix
Description: Nothing had ever established what keeps — or deletes — the images and plugin bundles in the fleet's own container registry, which already serves a live installation and is the default for new ones. It is now measured, declared, and held by a gate that re-reads the chart rather than the claim.
Icon: ShieldLock
Order: -20260914
---

# The fleet's own registry now says what deletes from it

MeshWeaver runs its own container registry. Every newly provisioned instance pulls its portal images
from it by default, one installation already does, and the plugin bundles an installation downloads
live there too.

**Nothing said what keeps any of it.** The fleet's retention work — the thirty-day window, the
protection that stops a cleanup deleting something still in use — was written for a *different*
registry, Microsoft's, and none of it can reach this one. So the question "what happens to an image
here when it gets old?" had never been asked, let alone answered.

## What was measured

Nothing deletes. Not a manifest, not a tag, not a layer that anything still refers to.

- The **one** automatic deletion the registry performs removes the scratch state of uploads that
  never finished, after a week. It cannot touch anything that was actually published.
- The command that permanently removes unreferenced data is not scheduled anywhere, has never been
  run, and exists in no configuration.
- Nothing in the delivery pipeline ever deletes from the registry. It only ever pushes.
- Only one account is permitted to delete at all — the publisher that CI uses. An installation,
  holding the key it pulls with, is refused if it tries.

One question is left for the people who own the storage account, and it is named rather than
assumed: whether a storage-level expiry rule is configured there. That one cannot be answered from
anything in this repository, and it matters, because expiring a layer under a registry does not make
an image look old — it makes it look present until somebody tries to pull it.

## Why "nothing deletes" is the answer and not the end of it

The registry grows without bound. That costs money, and it is the *safe* direction to be wrong in.
The danger is the obvious fix.

**This registry has no lock.** On the other registry, the fleet protects what is still in use by
marking it: a nightly job works out what every installation is running and what every committed file
pins, and writes that protection *into* the registry, where the registry itself enforces it. If the
job has a bad night, yesterday's protection is still there.

Here there is nothing to mark. A cleanup would have to work out what is still needed and delete
everything else in the same breath — so if it got that wrong once, it would delete once, with
nothing standing behind it.

So the rule that has been written down is deliberately stricter than the one for the other registry:
a cleanup may not exist until it can work out what is still needed; a run that could not see
everything deletes **nothing** rather than deleting less; and permanently removing data is a separate
act, days later, never part of the same operation.

## What changes now

**The declaration is committed and checked.** Every registry the fleet uses now has to state what
deletes from it, and the check does not simply re-read that statement — it re-reads the registry's
own configuration and says so when the two disagree. A scheduled cleanup job added beside the
registry, a deletion window quietly moved, or **the single permission rule that lets anything delete
at all being widened beyond the one account that has it**, each now fails a check that names what
changed, rather than sitting next to a sentence that says nothing deletes.

That last one is the reason the check reads the configuration rather than the statement. Who may
delete is not something a search through the code can answer — it is one rule in the registry's own
permission list, and a sentence saying "only the publisher can delete" reads exactly the same on the
day that rule is changed to say "anyone".

**The nightly protection run now prints what it works out.** It has always been able to see which
images the fleet's committed files pin in this registry; it used to throw that away. It now lists
them — image, version, and the file that pins each one. That list is what any future cleanup would
have to keep, published where somebody can check it, since a registry with nothing to mark has only
the working-out.

It also says what it is *not*. The list covers portal images. It does not cover plugin bundles,
because nothing today can enumerate what the registry actually holds — and a list that is complete
for one kind of thing and empty for another, reported as a single number, is exactly the mistake all
of this exists to prevent.
