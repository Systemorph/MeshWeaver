---
Name: The local portal check proves the controls are live
Category: Fix
Description: memex-local's usability check now reads the portal's own boot report instead of looking for files on disk, and it covers the pack that draws every input — so "usable" can no longer mean a portal nobody can type into.
Icon: Checkmark
Order: -20260907
---

# The local portal check proves the controls are live

`memex-local verify` exists to answer one question a green rollout cannot: is this portal actually
usable? It checked three things, and the third — are the view packs there? — was answering a
different question from the one it printed.

Two gaps, one shape. It looked for two packs and there are three: the missing one is the pack that
draws every input in the product — text, number, date, choice, plus the editor and edit-form
layouts. A portal without it can be read and not operated: no quiz answered, no coupon redeemed, no
dialog filled in. It passed.

And for the packs it did look at, it asked whether a file existed on disk. That is not the same as
the portal having loaded it — a pack whose bytes are present but which nothing at startup asked for
is inert, and its controls still print as text. That state was observed and reported as *present*.

The check now reads the portal's own startup report of which module it loaded from where — the same
list the portal then loads, so the line and the load cannot disagree — and it names any pack that is
on disk and not running. If that report is not in the log at all, the check says it could not tell
and fails, rather than reporting a pass it did not earn.
