---
Name: A change that spans two repositories now lands in the right order
Category: Feature
Description: When a platform change removes public code that another repository still uses, CI now refuses to merge it until the other half has landed. The breakage used to appear hours later, in a different repository, on work that had nothing to do with it.
Icon: Link
Order: -20260902
---

# A change that spans two repositories now lands in the right order

Some changes are two halves in two repositories: the platform gives something up, and a plugin
repository picks it up. Which half lands first decides whether anybody notices. If the picking-up
half lands first, nothing happens — the two exist side by side for a while. If the giving-up half
lands first, the other repository stops building.

That is not a hypothetical. When a set of platform views moved out to a module, the platform half
merged while the module half was still open. Every pull request in the other repository went red
within minutes, and its main branch stayed red for two hours — on a change none of those pull
requests had made. The people who saw the failure were never the people who caused it, and nothing
on the causing pull request had gone red to warn them.

Nothing could have caught it, either: the platform's checks do not build the other repository, and
the other repository's checks build against the last *published* platform rather than the one being
merged. The evidence simply did not exist anywhere until both halves were already combined.

**A platform pull request that removes public code now has to say what it pairs with.** One line in
the description names the counterpart — or states that there isn't one, and why — and the check
resolves it and refuses to go green while that counterpart is still open, still a draft, or merged
somewhere other than its repository's main branch. The giving-up half lands last, which is the only
order that never breaks anyone.

Ordinary work is untouched: measured over the last twenty-five platform changes, none removed public
code at all, so none of them meets this check.
