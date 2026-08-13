---
Name: A deployed change now actually takes effect
Category: Fix
Description: After a successful rebuild, pages could keep running the previous version of the code.
Icon: Sparkle
Order: -20260812
---

# A deployed change now actually takes effect

You could deploy a change, watch it rebuild successfully, and still be served the old behaviour.
Everything reported success — the type showed as compiled, against the new source, and its tests
ran green — while the pages themselves carried on running the previous build. The only way to
notice was to look for something you knew you had changed and find it missing.

The cause was narrow: a page that had loaded a *working* version of the code was never told when a
newer one was published. Pages that came up while something was broken already watched for a fix
and healed themselves; the ones that were fine had nothing watching them, because nothing appeared
to be wrong.

Every page now notices when its type publishes a new build and reloads itself against it, so a
successful deploy reaches the screen without anyone restarting or recycling anything by hand. A
rebuild that produces the *same* code changes nothing, so ordinary edits to a type — release notes,
a scheduled rebuild — no longer disturb pages that are already up to date.
