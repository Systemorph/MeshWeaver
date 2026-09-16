---
Name: A seal index that cannot be read holds instead of opening
Category: Fix
Description: A sync source advances only to the commit sealed for the instance's framework identity. When the sealed index could not be read at all, the empty result read as "nothing is sealed for me" and every repository was allowed to advance — the rule switching itself off with nothing red anywhere. It now holds.
Icon: ShieldError
Order: -20260916
---

# A seal index that cannot be read holds instead of opening

A GitSynced Space advances only to a commit **sealed for the framework identity its instance
runs**. The gate decides that from the list of publications sealed for this identity, and answers
"not my business, carry on" when the list is empty — which is right for an instance that runs no
publication of the repository at all.

Until now that same empty list was also what a **failed read** produced. A published root that
could not be enumerated therefore did not stop anything: it let **every** repository advance, which
is the exact opposite of the rule, and the only trace was one warning in a log nothing checks.

The reading now says which it is, and a failure holds every source rather than opening them:

> the publication index for this instance's framework identity `sd608997…` could not be READ — that
> is an absence of measurement, not an empty index, so every source is held rather than advanced.
> 'Cannot tell' is never 'clear to proceed'

Two states that look alike are kept apart deliberately. **Nothing** at the identity's path is
ordinary — a framework line nobody has published for yet — and still reads as a clean empty, because
treating it as a failure would freeze every source on every new platform line. **Something that is
not an enumerable directory** at exactly that path is a failure, because that is what a
half-finished layout migration looks like from this reader.

Full detail: [Publication Seal Starvation](/Doc/Architecture/PublicationSealStarvation).
