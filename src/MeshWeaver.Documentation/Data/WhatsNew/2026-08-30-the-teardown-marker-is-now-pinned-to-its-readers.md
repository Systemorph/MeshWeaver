---
Name: The teardown marker is now pinned to its readers
Category: Fix
Description: Two classifiers decide "the owner is going away, ask again" by substring-matching an exception message that dozens of places produce, and nothing checked that the two halves still agreed — a reworded message silently turned a teardown into a real answer. A guard now runs the real classifiers over the real producers.
Icon: LinkSquare
Order: -20260830
---

# The teardown marker is now pinned to its readers

When a hub is going away, the honest answer to a caller is *"ask again"* — the address may be
recycling and about to come back. Two places in the platform decide that: the mesh's node-stream
cache, which must not record a missing-node entry for an address that will return, and the GUI's
area classifier, which must not paint a terminal error over a view that is about to rehydrate.

Both decide it by **matching text in the exception message**. Dozens of places produce that message.
Nothing checked that the two halves still agreed.

That is not a theoretical concern. It has already been paid for once, and the bill was legible only
because someone happened to be running a flake reproduction a hundred times:

| branch | failures / 100 runs |
|---|---|
| before the fix | 20 |
| the fix, first draft | **10** — the author had reworded the message and dropped the marker |
| the fix, marker restored | 1 |

**Half the remaining failures in that draft were a broken pairing**, and ordinary CI would have
shipped it. It would then have surfaced as an unrelated intermittent somewhere else entirely.

A guard now closes it, and deliberately does *not* assert a spelling — checking the literal would be
the same defect one level up, a test holding its own copy of the string. It **constructs the real
producers and runs the real classifiers over what they actually say**. A producer whose wording
drifts fails, whatever the new wording is, because the classifier stops recognising it.

It also pins the two classifiers to each other. They are separate lists in separate assemblies, and
the platform's behaviour only makes sense while they agree: the cache riding a fault out while the
GUI treats it as terminal is a split brain that nothing else reports.
