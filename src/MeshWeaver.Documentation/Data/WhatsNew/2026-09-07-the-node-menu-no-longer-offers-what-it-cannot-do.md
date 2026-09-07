---
Name: The node menu no longer offers what it cannot do
Category: Fix
Description: Delete, Copy, Move and several other entries render from an optional package. Where that package is not installed, they were still offered and every click landed on an error page. The menu now only offers what this portal can actually carry out.
Icon: Bug
Order: -20260907
---

# The node menu no longer offers what it cannot do

Open any node's action menu and you are offered a set of operations — Edit, Copy, Move, Delete,
Versions, Files and so on. Several of those pages are not part of the platform itself: they arrive
with an optional package, the same way a chart type or a map does. On a portal that has the package,
everything works. On one that does not, the entries were offered exactly the same way — and clicking
one landed on a diagnostic page reading *"Area not found — no renderer is registered for area
Delete"*.

The menu now asks, for each entry it is about to offer, whether this portal actually has the page
behind it, and quietly leaves out the ones it does not. Nothing changes on a portal that has the
package: every entry that worked before still appears, in the same place, in the same order.

Two details were decided deliberately rather than by default.

An entry that runs a command in place rather than navigating — Recycle is the one that does — is
never affected, and neither is a submenu heading, a separator, or an entry that links somewhere other
than this node's own page. Only an entry that would take you to a page on this node, where no such
page exists, is left out.

And the check errs towards showing you too much rather than too little. If the portal cannot answer
the question reliably, every entry stays — because the failure mode of guessing "no" is that Delete,
Copy and Move quietly vanish everywhere, which is far worse than the error page this replaces. That
possibility is recognised by a specific signal, not assumed away.
