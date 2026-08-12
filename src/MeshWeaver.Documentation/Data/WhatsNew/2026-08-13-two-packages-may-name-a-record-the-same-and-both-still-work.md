---
Name: Two packages may name a record the same, and both still work
Category: Fix
Description: When two installed packages each defined a record with the same name, pages showing either of them fell back to an empty one. Content is now identified by the kind of node it lives on, so each package gets its own.
Icon: Sparkle
Order: -20260813
---

# Two packages may name a record the same, and both still work

A reinsurance space and an accounting space can both have a "Currency" — and in one customer
catalogue eleven names were shared that way. The platform used to keep a single list of those names
for the whole mesh, so the second package to load quietly took the first one's place, and content
from one package was read as the other's shape.

That guessing was removed earlier: a name claimed by two packages is now treated as ambiguous rather
than resolved to whichever compiled last. Honest, but it left a gap — pages showing content under a
shared name got nothing back, and the ones written to fall back to a blank record showed exactly
that: a page of empty fields, with nothing in the logs to explain it.

The name was never the right thing to look it up by. Content is now identified by **the kind of node
it lives on**, which is unique across the whole mesh by construction — so each package's records
resolve to that package's own shape, and a name two packages share stops being a problem to be
detected at all. The shared-name fallback is still there for the cases the node's kind cannot cover,
and it still declines to guess.

Pages that had been rendering blank under a shared name now show their real content again.
